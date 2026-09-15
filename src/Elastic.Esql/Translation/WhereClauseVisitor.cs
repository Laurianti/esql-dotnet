// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Elastic.Esql.Core;
using Elastic.Esql.Extensions;
using Elastic.Esql.Functions;

namespace Elastic.Esql.Translation;

/// <summary>
/// Translates LINQ predicate expressions to ES|QL WHERE conditions.
/// </summary>
internal sealed class WhereClauseVisitor(EsqlTranslationContext context) : ExpressionVisitor
{
	private readonly EsqlTranslationContext _context = context ?? throw new ArgumentNullException(nameof(context));
	private readonly StringBuilder _builder = new();
	private MemberInfo? _comparisonPropertyContext;

	/// <summary>
	/// Translates a predicate expression to an ES|QL condition string.
	/// </summary>
	public string Translate(Expression expression)
	{
		_ = _builder.Clear();
		_ = Visit(expression);
		return _builder.ToString();
	}

	protected override Expression VisitBinary(BinaryExpression node)
	{
		// a.CompareTo(b) > 0 and string.Compare(a, b) > 0 are the idiomatic way to
		// order strings in LINQ: rewrite them into a direct comparison, which ES|QL
		// supports natively on keyword fields.
		if (TryVisitStringComparison(node))
			return node;

		if (TryVisitRootNullGuard(node))
			return node;

		if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
		{
			var nullOp = node.NodeType == ExpressionType.Equal ? "IS NULL" : "IS NOT NULL";

			if (IsNullConstant(node.Right))
			{
				_ = Visit(node.Left);
				_ = _builder.Append(' ').Append(nullOp);
				return node;
			}

			if (IsNullConstant(node.Left))
			{
				_ = Visit(node.Right);
				_ = _builder.Append(' ').Append(nullOp);
				return node;
			}
		}

		// Only add parentheses for logical operators (AND/OR) to ensure proper grouping
		var isLogicalOperator = node.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse;

		if (isLogicalOperator)
			_ = _builder.Append('(');

		var enumComparison = TryGetEnumComparison(node);
		if (enumComparison.HasValue && !IsSpecialEnumAccess(enumComparison.Value.MemberSide.Member))
		{
			var propertyMember = enumComparison.Value.MemberSide.Member;
			_ = Visit(enumComparison.Value.MemberSide);
			var op = GetOperator(node.NodeType);
			_ = _builder.Append(' ').Append(op).Append(' ');

			var constant = enumComparison.Value.ConstantSide;
			var constantValue = ExpressionConstantResolver.Resolve(constant);
			var enumValue = constantValue is not null ? Enum.ToObject(enumComparison.Value.EnumType, constantValue) : null;

			_ = constant is MemberExpression member
				? _builder.Append(_context.GetValueOrParameterName(member.Member.Name, enumValue, propertyMember))
				: _builder.Append(_context.FormatValue(enumValue, propertyMember));
		}
		else
		{
			_comparisonPropertyContext = ExtractEntityPropertyMember(node);
			_ = Visit(node.Left);
			var op = GetOperator(node.NodeType);
			_ = _builder.Append(' ').Append(op).Append(' ');
			_ = Visit(node.Right);
			_comparisonPropertyContext = null;
		}

		if (isLogicalOperator)
			_ = _builder.Append(')');

		return node;
	}

	/// <summary>
	/// Inspects a <see cref="BinaryExpression"/> and determines whether it represents an enum comparison. If so, returns the enum type, the member site
	/// expression (the property/field being compared), and the constant enum value. Handles both regular and nullable enums.
	/// </summary>
	private static (Type EnumType, MemberExpression MemberSide, Expression ConstantSide)? TryGetEnumComparison(BinaryExpression binary)
	{
		// TODO: We can probably make this more robust by explicitly looking for the parametrized member access as the source of truth for the enum type.

		if (binary.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual
			or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
			or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual))
			return null;

		// Try both orientations: member == constant and constant == member.
		return TryMatch(binary.Left, binary.Right) ?? TryMatch(binary.Right, binary.Left);

		static (Type EnumType, MemberExpression MemberSide, Expression ConstantSide)? TryMatch(Expression candidateMember, Expression candidateConstant)
		{
			var memberSide = candidateMember.UnwrapConvertExpressions();
			var constantSide = candidateConstant.UnwrapConvertExpressions();

			// Resolve the enum type from whichever side actually has it.
			// The member side is authoritative, but for `Nullable<TEnum> == null` the constant side may be typed differently.
			var enumType = GetEnumType(memberSide.Type);
			if (enumType is null)
				return null;

			// The member side must be a member access.
			if (memberSide is not MemberExpression memberExpression)
				return null;

			// The constant side must be a static- or closure-rooted expression that can be resolved to a value.
			// Cases where both sides are dependent on the input lambda parameter are dealt with as non-enum comparisons and don't require special handling.
			if (!constantSide.SupportsEvaluation())
				return null;

			return (enumType, memberExpression, constantSide);
		}

		static Type? GetEnumType(Type type)
		{
			var candidate = Nullable.GetUnderlyingType(type) ?? type;

			return candidate.IsEnum ? candidate : null;
		}
	}

	private static bool IsSpecialEnumAccess(MemberInfo member)
	{
		// DateTime/DateTimeOffset properties like DayOfWeek return enums but translate to
		// DATE_EXTRACT which produces integers — don't treat these as enum comparisons
		var declaringType = member.DeclaringType;
		return declaringType == typeof(DateTime) || declaringType == typeof(DateTimeOffset);
	}

	/// <summary>
	/// Extracts the entity property <see cref="MemberInfo"/> from a binary comparison so that
	/// property-level <see cref="System.Text.Json.Serialization.JsonConverterAttribute"/> can
	/// be respected when serializing the compared value.
	/// </summary>
	private static MemberInfo? ExtractEntityPropertyMember(BinaryExpression node)
	{
		if (node.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual
			or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
			or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual))
			return null;

		return TryExtract(node.Left) ?? TryExtract(node.Right);

		static MemberInfo? TryExtract(Expression expr)
		{
			var unwrapped = expr.UnwrapConvertExpressions();
			if (unwrapped is MemberExpression member && ExpressionTranslationHelpers.IsRootedInParameter(member))
				return member.Member;

			return null;
		}
	}

	protected override Expression VisitUnary(UnaryExpression node)
	{
		switch (node.NodeType)
		{
			case ExpressionType.Not:
				_ = _builder.Append("NOT ");
				_ = Visit(node.Operand);
				break;

			case ExpressionType.Convert:
			case ExpressionType.ConvertChecked:
				// Implicit/explicit conversion to a DenseVector<T> from a closure-captured
				// T[] / ReadOnlyMemory<T>. Resolve the converted value (the implicit operator
				// is invoked by ExpressionConstantResolver) and emit as a parameter / inline literal.
				if (TryEmitVectorConvert(node))
					return node;

				// Just visit the operand, ES|QL handles type coercion
				_ = Visit(node.Operand);
				break;

			default:
				throw new NotSupportedException($"Unary operator {node.NodeType} is not supported.");
		}

		return node;
	}

	private bool TryEmitVectorConvert(UnaryExpression node)
	{
		if (!DenseVectorTypeHelper.TryEmitDenseVectorLiteral(node, _context, out var literal))
			return false;

		_ = _builder.Append(literal);
		return true;
	}

	protected override Expression VisitMember(MemberExpression node)
	{
		// Check if this is accessing a captured variable (closure)
		if (node.Expression is ConstantExpression constantExpression)
		{
			var value = GetMemberValue(node, constantExpression.Value);
			_ = _builder.Append(_context.GetValueOrParameterName(node.Member.Name, value, _comparisonPropertyContext));
			_comparisonPropertyContext = null;
			return node;
		}

		// Check if this is a nested member access on a captured variable
		if (node.Expression is MemberExpression innerMember &&
			innerMember.Expression is ConstantExpression innerConstant)
		{
			var innerValue = GetMemberValue(innerMember, innerConstant.Value);
			var value = GetMemberValue(node, innerValue);
			_ = _builder.Append(_context.GetValueOrParameterName(node.Member.Name, value, _comparisonPropertyContext));
			_comparisonPropertyContext = null;
			return node;
		}

		// Check for static member access (like DateTime.UtcNow)
		if (node.Expression == null)
		{
			// Handle DateTime/DateTimeOffset static properties that should translate to NOW()
			var declaringType = node.Member.DeclaringType;
			var memberName = node.Member.Name;

			if (declaringType == typeof(DateTime) || declaringType == typeof(DateTimeOffset))
			{
				switch (memberName)
				{
					case "Now":
					case "UtcNow":
						_ = _builder.Append("NOW()");
						return node;
					case "Today":
						_ = _builder.Append("DATE_TRUNC(\"day\", NOW())");
						return node;
				}
			}

			// Math constants: Math.E, Math.PI, Math.Tau
			if (declaringType == typeof(Math))
			{
				var mathConst = EsqlFunctionTranslator.TryTranslateMathConstant(memberName);
				if (mathConst != null)
				{
					_ = _builder.Append(mathConst);
					return node;
				}
			}

			// EsqlMetadata.* marker access -> emit underscore-prefixed ES|QL identifier.
			if (declaringType == typeof(EsqlMetadata))
			{
				_ = _builder.Append(_context.ResolveMetadataMemberOrThrow(memberName));
				return node;
			}

			// For other static members, evaluate the value
			var value = GetStaticMemberValue(node);
			_ = _builder.Append(_context.FormatValue(value));
			return node;
		}

		// Handle string.Length property → LENGTH(field)
		if (node.Member.DeclaringType == typeof(string) && node.Member.Name == "Length")
		{
			_ = _builder.Append("LENGTH(");
			_ = Visit(node.Expression);
			_ = _builder.Append(')');
			return node;
		}

		// Check for DateTime/DateTimeOffset property access (Year, Month, Day, etc.)
		if (node.Member.DeclaringType == typeof(DateTime) || node.Member.DeclaringType == typeof(DateTimeOffset))
		{
			var dateExpr = TranslateDateTimeExpression(node.Expression);
			var memberName = node.Member.Name;

			switch (memberName)
			{
				case "Year":
					_ = _builder.Append("DATE_EXTRACT(\"year\", ").Append(dateExpr).Append(')');
					return node;
				case "Month":
					_ = _builder.Append("DATE_EXTRACT(\"month\", ").Append(dateExpr).Append(')');
					return node;
				case "Day":
					_ = _builder.Append("DATE_EXTRACT(\"day_of_month\", ").Append(dateExpr).Append(')');
					return node;
				case "Hour":
					_ = _builder.Append("DATE_EXTRACT(\"hour\", ").Append(dateExpr).Append(')');
					return node;
				case "Minute":
					_ = _builder.Append("DATE_EXTRACT(\"minute\", ").Append(dateExpr).Append(')');
					return node;
				case "Second":
					_ = _builder.Append("DATE_EXTRACT(\"second\", ").Append(dateExpr).Append(')');
					return node;
				case "DayOfWeek":
					_ = _builder.Append("DATE_EXTRACT(\"day_of_week\", ").Append(dateExpr).Append(')');
					return node;
				case "DayOfYear":
					_ = _builder.Append("DATE_EXTRACT(\"day_of_year\", ").Append(dateExpr).Append(')');
					return node;
			}
		}

		// Regular field access
		var fieldName = ResolveFieldPath(node);
		_ = _builder.Append(fieldName);

		return node;
	}

	private string TranslateDateTimeExpression(Expression expression) =>
		// Recursively translate the inner expression
		expression switch
		{
			MemberExpression member when member.Expression == null =>
				// Static property like DateTime.UtcNow
				TranslateStaticDateTimeProperty(member),
			MemberExpression member =>
				// Field access like l.Timestamp
				ResolveFieldPath(member),
			MethodCallExpression methodCall when methodCall.Method.DeclaringType == typeof(EsqlFunctions) =>
				TranslateEsqlFunctionForDateTime(methodCall),
			_ => throw new NotSupportedException($"Expression type {expression.GetType().Name} is not supported for DateTime property access.")
		};

	private string ResolveFieldPath(MemberExpression member)
	{
		var remainingPath = member.ResolveFieldName(_context.Metadata);

		foreach (var prefix in GetTransparentIdentifierPrefixes(member))
		{
			var prefixWithDot = $"{prefix}.";
			if (!remainingPath.StartsWith(prefixWithDot, StringComparison.Ordinal))
				break;

			remainingPath = remainingPath[prefixWithDot.Length..];
		}

		return remainingPath;
	}

	private IEnumerable<string> GetTransparentIdentifierPrefixes(MemberExpression member)
	{
		var chain = ExpressionTranslationHelpers.GetMemberChainFromRoot(member);
		foreach (var chainedMember in chain)
		{
			var declaringType = chainedMember.Member.DeclaringType;
			if (declaringType is null || !declaringType.IsDefined(typeof(CompilerGeneratedAttribute), false))
				yield break;

			if (_context.IsTrackedAnonymousType(declaringType))
				yield break;

			yield return _context.ResolveFieldName(declaringType, chainedMember.Member);
		}
	}

	private string TranslateStaticDateTimeProperty(MemberExpression member)
	{
		var memberName = member.Member.Name;
		return memberName switch
		{
			"Now" or "UtcNow" => "NOW()",
			"Today" => "DATE_TRUNC(\"day\", NOW())",
			_ => throw new NotSupportedException($"DateTime static property {memberName} is not supported.")
		};
	}

	private string TranslateEsqlFunctionForDateTime(MethodCallExpression methodCall)
	{
		var methodName = methodCall.Method.Name;
		var translated = EsqlFunctionTranslator.TryTranslateMethodCall(methodCall, TranslateDateTimeExpression);
		return translated ?? throw new NotSupportedException($"EsqlFunction {methodName} is not supported in DateTime context.");
	}

	protected override Expression VisitConstant(ConstantExpression node)
	{
		_ = _builder.Append(_context.FormatValue(node.Value, _comparisonPropertyContext));
		_comparisonPropertyContext = null;
		return node;
	}

	protected override Expression VisitMethodCall(MethodCallExpression node)
	{
		var methodName = node.Method.Name;
		var declaringType = node.Method.DeclaringType;

		// MultiField extension: l.Field.MultiField("keyword")
		if (declaringType == typeof(GeneralPurposeExtensions) && methodName == "MultiField")
		{
			_ = _builder.Append(node.ResolveFieldName(_context.Metadata));
			return node;
		}

		// Check for EsqlFunctions marker methods
		if (declaringType == typeof(EsqlFunctions))
			return VisitEsqlFunction(node);

		// String methods
		if (declaringType == typeof(string))
			return VisitStringMethod(node);

		// Math methods
		if (declaringType == typeof(Math))
			return VisitMathMethod(node);

		// Multi-value field predicates: tags.Any(t => t == "x"), tags.Contains("x").
		// Checked before the constant-collection IN translation, because there the
		// collection is a captured constant while here it is a document field.
		if (TryVisitMultiValueField(node))
			return node;

		if (methodName == "Contains" && TryVisitCollectionContains(node))
			return node;

		// DateTime methods
		if (declaringType == typeof(DateTime) || declaringType == typeof(DateTimeOffset))
			return VisitDateTimeMethod(node);

		// TimeSpan static methods
		if (declaringType == typeof(TimeSpan))
			return VisitTimeSpanMethod(node);

		throw new NotSupportedException($"Method {declaringType?.Name}.{methodName} is not supported.");
	}

	private Expression VisitTimeSpanMethod(MethodCallExpression node)
	{
		var methodName = node.Method.Name;
		var arg = GetConstantValue(node.Arguments[0]);

		// Convert the numeric value to appropriate ES|QL time interval
		return methodName switch
		{
			"FromDays" => AppendTimeInterval(arg, "days"),
			"FromHours" => AppendTimeInterval(arg, "hours"),
			"FromMinutes" => AppendTimeInterval(arg, "minutes"),
			"FromSeconds" => AppendTimeInterval(arg, "seconds"),
			"FromMilliseconds" => AppendTimeInterval(arg, "milliseconds"),
			_ => throw new NotSupportedException($"TimeSpan method {methodName} is not supported.")
		};
	}

	private Expression AppendTimeInterval(object? value, string unit)
	{
		// Format as ES|QL time interval (e.g., "1 hour", "30 minutes")
		_ = _builder.AppendFormat(CultureInfo.InvariantCulture, "{0} {1}", value, unit);
		return Expression.Empty();
	}

	private Expression VisitEsqlFunction(MethodCallExpression node)
	{
		var methodName = node.Method.Name;
		var result = EsqlFunctionTranslator.TryTranslateMethodCall(node, TranslateSubExpression);
		if (result != null)
		{
			_ = _builder.Append(result);
			return node;
		}

		throw new NotSupportedException($"ES|QL function {methodName} is not supported.");
	}

	private Expression VisitMathMethod(MethodCallExpression node)
	{
		var methodName = node.Method.Name;
		var result = EsqlFunctionTranslator.TryTranslateMethodCall(node, TranslateSubExpression);
		if (result != null)
		{
			_ = _builder.Append(result);
			return node;
		}

		throw new NotSupportedException($"Math method {methodName} is not supported.");
	}

	private string TranslateSubExpression(Expression expression)
	{
		var saved = _builder.ToString();
		_ = _builder.Clear();
		_ = Visit(expression);
		var result = _builder.ToString();
		_ = _builder.Clear().Append(saved);
		return result;
	}

	/// <summary>
	/// Whether the StringComparison argument asks for the ordering ES|QL performs.
	/// Only <see cref="StringComparison.Ordinal"/> does: keyword ordering is
	/// case-sensitive, so OrdinalIgnoreCase would order "a" and "B" the other way.
	/// </summary>
	private static bool IsOrdinalComparison(Expression expression)
	{
		try
		{
			return GetConstantValue(expression) is StringComparison.Ordinal;
		}
		catch (NotSupportedException)
		{
			return false;
		}
	}

	/// <summary>
	/// "p != null" on the lambda parameter itself: the document is never null, and
	/// there is no field to put in front of IS NOT NULL, so the guard is a constant.
	/// </summary>
	private bool TryVisitRootNullGuard(BinaryExpression node)
	{
		if (node.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
			return false;

		var parameter = node.Left is ParameterExpression left && ResolvesToNullConstant(node.Right) ? left
			: node.Right is ParameterExpression right && ResolvesToNullConstant(node.Left) ? right
			: null;

		if (parameter is null)
			return false;

		// Only the document row is known never to be null, and there the guard is a
		// constant. After a projection the parameter stands for the projected value,
		// which has no field name of its own to compare, so the shape is refused
		// rather than folded into a constant that would drop every row.
		if (!IsDocumentParameter(parameter))
		{
			throw new NotSupportedException(
				"A null comparison against a projected value is not supported: compare the "
				+ "document field instead, before the projection.");
		}

		_ = _builder.Append(node.NodeType == ExpressionType.Equal ? "FALSE" : "TRUE");
		return true;
	}

	/// <summary>Whether the parameter stands for the document row rather than a projected value.</summary>
	private bool IsDocumentParameter(ParameterExpression parameter) =>
		!parameter.Type.IsValueType
		&& parameter.Type != typeof(string)
		&& (_context.ElementType is null || parameter.Type == _context.ElementType);

	/// <summary>
	/// Rewrites <c>a.CompareTo(b) &gt; 0</c> or <c>string.Compare(a, b) &gt; 0</c> into
	/// <c>a &gt; b</c>. Only comparisons against the constant zero are handled, which is
	/// the only shape that carries an ordering meaning.
	/// <para>
	/// The ordering itself is the one Elasticsearch applies to a keyword field, which
	/// is ordinal: <c>"B"</c> sorts before <c>"a"</c>. In .NET these overloads are
	/// culture-sensitive and would answer the other way round, so the two agree only
	/// for values that order the same either way. Sorting and paging over a keyword
	/// field are ordinal to begin with, which is what this rewrite exists for.
	/// </para>
	/// </summary>
	private bool TryVisitStringComparison(BinaryExpression node)
	{
		if (node.NodeType is not (ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
			or ExpressionType.LessThan or ExpressionType.LessThanOrEqual))
			return false;

		var (call, zero, flipped) = node.Left is MethodCallExpression left
			? (left, node.Right, false)
			: node.Right is MethodCallExpression right ? (right, node.Left, true) : (null, null, false);

		if (call is null || zero is not ConstantExpression { Value: 0 })
			return false;

		if (call.Method.DeclaringType != typeof(string)
			|| call.Method.Name is not ("CompareTo" or "Compare" or "CompareOrdinal"))
			return false;

		var parameters = call.Method.GetParameters();

		// the values compared have to be strings: CompareTo(object) orders something else
		if (parameters.Take(call.Object is not null ? 1 : 2).Any(parameter => parameter.ParameterType != typeof(string)))
			return false;

		// instance form: a.CompareTo(b); static form: string.Compare(a, b), with an
		// optional StringComparison that has to be an ordinal one
		var (first, second, comparison) = call.Object is not null
			? parameters.Length == 1 ? (call.Object, call.Arguments[0], (Expression?)null) : (null, null, null)
			: parameters.Length is 2 or 3 ? (call.Arguments[0], call.Arguments[1], parameters.Length == 3 ? call.Arguments[2] : null)
			: (null, null, null);

		if (first is null || second is null)
			return false;

		if (parameters.Length == 3 && parameters[2].ParameterType != typeof(StringComparison))
			return false;

		// ES|QL orders keyword values by their bytes. An explicitly ordinal comparison
		// means exactly that; a culture-sensitive one means something else, and is
		// refused rather than answered with an ordering it did not ask for.
		if (comparison is not null && !IsOrdinalComparison(comparison))
		{
			throw new NotSupportedException(
				$"String method {call.Method.Name} is only supported with StringComparison.Ordinal: "
				+ "keyword ordering is ordinal and case-sensitive, so any other comparison mode "
				+ "asks for an ordering Elasticsearch does not apply.");
		}

		// .NET orders a non-null string above null, which a plain ES|QL comparison
		// against null does not reproduce
		if (ResolvesToNullConstant(first) || ResolvesToNullConstant(second))
			return false;

		var op = node.NodeType switch
		{
			ExpressionType.GreaterThan => flipped ? "<" : ">",
			ExpressionType.GreaterThanOrEqual => flipped ? "<=" : ">=",
			ExpressionType.LessThan => flipped ? ">" : "<",
			_ => flipped ? ">=" : "<="
		};

		_ = Visit(first);
		_ = _builder.Append(' ').Append(op).Append(' ');
		_ = Visit(second);

		return true;
	}

	private Expression VisitStringMethod(MethodCallExpression node)
	{
		var methodName = node.Method.Name;

		switch (methodName)
		{
			case "Contains":
				// string.Contains("x") → LIKE "*x*"
				_ = Visit(node.Object);
				_ = _builder.Append(" LIKE ");
				var containsValue = GetConstantValue(node.Arguments[0]);
				_ = _builder.Append("\"*").Append(EscapeLikePattern(containsValue?.ToString() ?? "")).Append("*\"");
				break;

			case "StartsWith":
				// string.StartsWith("x") → LIKE "x*"
				_ = Visit(node.Object);
				_ = _builder.Append(" LIKE ");
				var startsValue = GetConstantValue(node.Arguments[0]);
				_ = _builder.Append('"').Append(EscapeLikePattern(startsValue?.ToString() ?? "")).Append("*\"");
				break;

			case "EndsWith":
				// string.EndsWith("x") → LIKE "*x"
				_ = Visit(node.Object);
				_ = _builder.Append(" LIKE ");
				var endsValue = GetConstantValue(node.Arguments[0]);
				_ = _builder.Append("\"*").Append(EscapeLikePattern(endsValue?.ToString() ?? "")).Append('"');
				break;

			case "CompareTo":
			case "Compare":
			case "CompareOrdinal":
				// string.CompareTo(x) and string.Compare(a, b) only appear inside a
				// comparison against zero, which the binary visitor rewrites into a
				// direct comparison between the two operands.
				throw new NotSupportedException(
					$"String method {methodName} is only supported when compared to zero, "
					+ "for example a.CompareTo(b) > 0.");

			case "IsNullOrEmpty":
				_ = _builder.Append('(');
				_ = Visit(node.Arguments[0]);
				_ = _builder.Append(" IS NULL OR ");
				_ = Visit(node.Arguments[0]);
				_ = _builder.Append(" == \"\")");
				break;

			case "IsNullOrWhiteSpace":
				_ = _builder.Append('(');
				_ = Visit(node.Arguments[0]);
				_ = _builder.Append(" IS NULL OR TRIM(");
				_ = Visit(node.Arguments[0]);
				_ = _builder.Append(") == \"\")");
				break;

			case "get_Chars":
				// string[i] → SUBSTRING(s, i+1, 1)
				_ = _builder.Append("SUBSTRING(");
				_ = Visit(node.Object);
				_ = _builder.Append(", ");
				// Add 1 for 1-based indexing in ES|QL
				var index = GetConstantValue(node.Arguments[0]);
				if (index is int idx)
					_ = _builder.Append(idx + 1);
				else
				{
					_ = _builder.Append('(');
					_ = Visit(node.Arguments[0]);
					_ = _builder.Append(") + 1");
				}

				_ = _builder.Append(", 1)");
				break;

			default:
				var result = EsqlFunctionTranslator.TryTranslateMethodCall(node, TranslateSubExpression);
				if (result != null)
				{
					_ = _builder.Append(result);
					break;
				}

				throw new NotSupportedException($"String method {methodName} is not supported.");
		}

		return node;
	}

	private Expression VisitDateTimeMethod(MethodCallExpression node)
	{
		var methodName = node.Method.Name;

		switch (methodName)
		{
			case "AddDays":
			case "AddHours":
			case "AddMinutes":
			case "AddSeconds":
			case "AddMilliseconds":
				// DateTime arithmetic
				_ = _builder.Append('(');
				_ = Visit(node.Object);
				var amount = GetConstantValue(node.Arguments[0]);
				var unit = methodName.Replace("Add", "").ToLowerInvariant();
				_ = amount is double d and < 0
					? _builder.AppendFormat(CultureInfo.InvariantCulture, " - {0} {1}", Math.Abs(d), unit)
					: _builder.AppendFormat(CultureInfo.InvariantCulture, " + {0} {1}", amount, unit);
				_ = _builder.Append(')');
				break;

			default:
				throw new NotSupportedException($"DateTime method {methodName} is not supported.");
		}

		return node;
	}

	/// <summary>
	/// How many values of a multi-value field a predicate inspects, one position at a
	/// time. MV_SLICE reads a value by position, so the number of positions has to be
	/// fixed when the query is written; a field holding more values than this cannot be
	/// decided from the positions read, and the predicate is null for it.
	/// </summary>
	private const int MaxInspectedValues = 32;

	private enum ElementPredicateKind
	{
		Equal,
		In,
		StartsWith,
		EndsWith,
		Contains,
		GreaterThan,
		GreaterThanOrEqual,
		LessThan,
		LessThanOrEqual
	}

	/// <summary>A predicate over one value of a multi-value field, e.g. "x == 42".</summary>
	private readonly record struct ElementPredicate(ElementPredicateKind Kind, IReadOnlyList<object?> Values, bool Negated);

	/// <summary>
	/// Predicates over a multi-value document field: <c>field.Any(...)</c>,
	/// <c>field.All(...)</c> and <c>field.Contains(value)</c>. A document holds every
	/// value of the field at once, so the quantifier is answered on the field itself,
	/// without the row duplication MV_EXPAND would introduce.
	/// </summary>
	private bool TryVisitMultiValueField(MethodCallExpression node)
	{
		var methodName = node.Method.Name;

		if (methodName is not ("Any" or "All" or "Contains"))
			return false;

		// the source must be a document field, not a constant collection
		var source = node.Method.IsStatic
			? node.Arguments.Count > 0 ? node.Arguments[0] : null
			: node.Object;

		// arrays reach us through MemoryExtensions.Contains(ReadOnlySpan<T>, T)
		if (source is not null && node.Method.DeclaringType == typeof(MemoryExtensions))
			source = TryUnwrapMemoryExtensionsSource(source);

		if (source is null || !IsMultiValueField(source))
			return false;

		// field.Any() with no predicate: the field simply has to hold a value
		if (methodName == "Any" && node.Arguments.Count == (node.Method.IsStatic ? 1 : 0))
		{
			// MV_COUNT is null over a missing field, and so would be the negation; LINQ
			// reads a missing field as an empty sequence, where Any() is simply false
			_ = _builder.Append("COALESCE(MV_COUNT(").Append(source.ResolveFieldName(_context.Metadata)).Append("), 0) > 0");
			return true;
		}

		// field.Contains(value), and only that overload: one taking a comparer asks
		// for a comparison the translation cannot honour
		if (methodName == "Contains")
		{
			var expectedArguments = node.Method.IsStatic ? 2 : 1;

			return node.Arguments.Count == expectedArguments
				&& TryAppendMatch(source, node.Arguments[^1]);
		}

		var argument = node.Arguments[^1];

		if (StripQuotes(argument) is not LambdaExpression { Parameters.Count: 1 } lambda)
			return false;

		var predicate = TryParseElementPredicate(lambda.Body, lambda.Parameters[0]);
		if (predicate is null)
			return false;

		return TryAppendQuantified(source, lambda.Parameters[0].Type, methodName == "All", predicate.Value);
	}

	/// <summary>
	/// Reads the body of the lambda passed to Any/All as one predicate on the element.
	/// Null guards on the element are dropped, since a stored value is never null.
	/// </summary>
	private static ElementPredicate? TryParseElementPredicate(Expression body, ParameterExpression element, bool negated = false)
	{
		switch (body)
		{
			case UnaryExpression { NodeType: ExpressionType.Not } negation:
				return TryParseElementPredicate(negation.Operand, element, !negated);

			// "x != null && P(x)" is P(x)
			case BinaryExpression { NodeType: ExpressionType.AndAlso } conjunction when IsNullGuard(conjunction.Left, element, ExpressionType.NotEqual):
				return TryParseElementPredicate(conjunction.Right, element, negated);

			case BinaryExpression { NodeType: ExpressionType.AndAlso } conjunction when IsNullGuard(conjunction.Right, element, ExpressionType.NotEqual):
				return TryParseElementPredicate(conjunction.Left, element, negated);

			// "x == null || P(x)" is P(x)
			case BinaryExpression { NodeType: ExpressionType.OrElse } disjunction when IsNullGuard(disjunction.Left, element, ExpressionType.Equal):
				return TryParseElementPredicate(disjunction.Right, element, negated);

			case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } comparison:
			{
				var value = comparison.Left == element ? comparison.Right
					: comparison.Right == element ? comparison.Left
					: null;

				if (value is null || !TryGetConstant(value, out var constant))
					return null;

				var isEqual = comparison.NodeType == ExpressionType.Equal;
				return new ElementPredicate(ElementPredicateKind.Equal, [constant], isEqual ? negated : !negated);
			}

			case BinaryExpression
			{
				NodeType: ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
					or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
			} ordering:
			{
				// "10 < x" is "x > 10": keep the element on the left
				var elementOnLeft = ordering.Left == element;
				var value = elementOnLeft ? ordering.Right : ordering.Right == element ? ordering.Left : null;

				if (value is null || !TryGetConstant(value, out var constant))
					return null;

				var kind = (ordering.NodeType, elementOnLeft) switch
				{
					(ExpressionType.GreaterThan, true) or (ExpressionType.LessThan, false) => ElementPredicateKind.GreaterThan,
					(ExpressionType.GreaterThanOrEqual, true) or (ExpressionType.LessThanOrEqual, false) => ElementPredicateKind.GreaterThanOrEqual,
					(ExpressionType.LessThan, true) or (ExpressionType.GreaterThan, false) => ElementPredicateKind.LessThan,
					_ => ElementPredicateKind.LessThanOrEqual
				};

				return new ElementPredicate(kind, [constant], negated);
			}

			case MethodCallExpression call:
			{
				// x.StartsWith("a"), x.EndsWith("a"), x.Contains("a")
				if (call.Object == element && call.Method.DeclaringType == typeof(string) && call.Arguments.Count == 1)
				{
					if (!TryGetTextPredicateKind(call.Method.Name, out var kind)
						|| !TryGetConstant(call.Arguments[0], out var constant))
						return null;

					return new ElementPredicate(kind, [constant], negated);
				}

				// values.Contains(x), over a constant collection
				if (TryGetContainsArguments(call, out var valueExpression, out var collection)
					&& valueExpression == element
					&& collection is not null)
				{
					var candidates = collection.Cast<object?>().ToList();

					// a stored value is never null, and MATCH(field, null) is not valid ES|QL
					if (candidates.Any(candidate => candidate is null))
						return null;

					return new ElementPredicate(ElementPredicateKind.In, candidates, negated);
				}

				return null;
			}

			default:
				return null;
		}
	}

	/// <summary>The kind of the string predicates MATCH cannot answer on its own.</summary>
	private static bool TryGetTextPredicateKind(string methodName, out ElementPredicateKind kind)
	{
		switch (methodName)
		{
			case "StartsWith":
				kind = ElementPredicateKind.StartsWith;
				return true;

			case "EndsWith":
				kind = ElementPredicateKind.EndsWith;
				return true;

			case "Contains":
				kind = ElementPredicateKind.Contains;
				return true;

			default:
				kind = default;
				return false;
		}
	}

	private static bool IsNullGuard(Expression expression, ParameterExpression element, ExpressionType comparison) =>
		expression is BinaryExpression binary
		&& binary.NodeType == comparison
		&& ((binary.Left == element && IsNullConstant(binary.Right)) || (binary.Right == element && IsNullConstant(binary.Left)));

	/// <summary>
	/// The constant a predicate compares a field value against. Null is refused: a
	/// multi-value field stores no null element, so there is nothing to match, and
	/// MATCH(field, null) is not valid ES|QL.
	/// </summary>
	private static bool TryGetConstant(Expression expression, out object? value)
	{
		try
		{
			value = GetConstantValue(expression);
			return value is not null;
		}
		catch (NotSupportedException)
		{
			value = null;
			return false;
		}
	}

	/// <summary>
	/// Any(P) and All(P) over the values of a field. A negated predicate is pushed into
	/// the quantifier, since Any(not P) is "not All(P)" and All(not P) is "not Any(P)".
	/// </summary>
	private bool TryAppendQuantified(Expression field, Type elementType, bool all, ElementPredicate predicate)
	{
		var name = field.ResolveFieldName(_context.Metadata);

		// "All(not P)" is "not Any(P)": the quantifier flips, but a missing field still
		if (predicate.Negated)
		{
			_ = _builder.Append("NOT ");
			all = !all;
			predicate = predicate with { Negated = false };
		}

		switch (predicate.Kind)
		{
			// a document matches MATCH when any of the field's values does
			case ElementPredicateKind.Equal when !all:
				AppendMatch(name, predicate.Values[0]);
				return true;

			case ElementPredicateKind.In when !all:
				if (predicate.Values.Count == 0)
				{
					_ = _builder.Append("FALSE");
					return true;
				}

				if (predicate.Values.Count > 1)
					_ = _builder.Append('(');

				for (var i = 0; i < predicate.Values.Count; i++)
				{
					if (i > 0)
						_ = _builder.Append(" OR ");

					AppendMatch(name, predicate.Values[i]);
				}

				if (predicate.Values.Count > 1)
					_ = _builder.Append(')');

				return true;

			// every value equals v: the field holds one distinct value, and it matches.
			// A missing field has no value that differs, as All() over an empty sequence is true.
			case ElementPredicateKind.Equal:
				_ = _builder.Append('(').Append(name).Append(" IS NULL OR (MV_COUNT(MV_DEDUPE(").Append(name).Append(")) == 1 AND ");
				AppendMatch(name, predicate.Values[0]);
				_ = _builder.Append("))");
				return true;

			// some value is above v when the largest is; every value is when the smallest is
			case ElementPredicateKind.GreaterThan or ElementPredicateKind.GreaterThanOrEqual
				or ElementPredicateKind.LessThan or ElementPredicateKind.LessThanOrEqual:
			{
				var upper = predicate.Kind is ElementPredicateKind.GreaterThan or ElementPredicateKind.GreaterThanOrEqual;
				var aggregate = all == upper ? "MV_MIN" : "MV_MAX";
				var op = predicate.Kind switch
				{
					ElementPredicateKind.GreaterThan => ">",
					ElementPredicateKind.GreaterThanOrEqual => ">=",
					ElementPredicateKind.LessThan => "<",
					_ => "<="
				};

				// MV_MIN and MV_MAX are null over a missing field, and so would be the
				// whole predicate, which then answers neither true nor false. A missing
				// field is an empty sequence: All holds over it and Any does not, and
				// saying so explicitly keeps an enclosing NOT meaningful.
				_ = _builder.Append('(').Append(name).Append(all ? " IS NULL OR " : " IS NOT NULL AND ");

				_ = _builder.Append(aggregate).Append('(').Append(name).Append(") ").Append(op).Append(' ')
					.Append(_context.FormatValue(predicate.Values[0], null)).Append(')');

				return true;
			}

			default:
				return TryAppendValuePattern(name, elementType, all, predicate);
		}
	}

	private void AppendMatch(string field, object? value) =>
		_ = _builder.Append("MATCH(").Append(field).Append(", ").Append(_context.FormatValue(value, null)).Append(')');

	/// <summary>
	/// Predicates MATCH cannot answer, such as "starts with". ES|QL applies a scalar
	/// function to a single value, not to every value of a field at once, but MV_SLICE
	/// reads a value by position, so the test is written out once per position and
	/// combined: any of them for Any, all of them for All.
	/// <para>
	/// Only the first <see cref="MaxInspectedValues"/> positions are read, so a field
	/// holding more values than that cannot be decided from them. The predicate is null
	/// for such a field rather than false, since false would let an enclosing NOT turn it
	/// into a match, and WHERE drops a null row either way.
	/// </para>
	/// </summary>
	private bool TryAppendValuePattern(string field, Type elementType, bool all, ElementPredicate predicate)
	{
		var isString = elementType == typeof(string);
		var isIntegral = elementType == typeof(int) || elementType == typeof(long)
			|| elementType == typeof(short) || elementType == typeof(byte);

		// text predicates need text; equality needs a value whose text form is
		// unambiguous, which rules out floating-point fields
		if (!isString && (!isIntegral || predicate.Kind is not (ElementPredicateKind.Equal or ElementPredicateKind.In)))
			return false;

		// numbers are rendered the way Elasticsearch renders them, which is invariant;
		// the current culture could otherwise introduce separators of its own
		var values = predicate.Values
			.Select(value => value is IFormattable formattable
				? formattable.ToString(null, CultureInfo.InvariantCulture)
				: value?.ToString() ?? "")
			.ToList();

		// All over an empty list holds only for the empty field; Any never does
		if (predicate.Kind == ElementPredicateKind.In && values.Count == 0)
		{
			_ = _builder.Append(all ? field + " IS NULL" : "FALSE");
			return true;
		}

		// A field holding more values than the positions read cannot be answered from
		// those positions alone, and saying "false" would let an enclosing NOT turn it
		// into a match. The predicate is null there instead, which WHERE drops either
		// way, so such a document is left out of the result rather than answered wrongly.
		_ = _builder.Append("CASE(MV_COUNT(").Append(field).Append(") > ").Append(MaxInspectedValues).Append(", NULL, (");

		for (var position = 0; position < MaxInspectedValues; position++)
		{
			if (position > 0)
				_ = _builder.Append(all ? " AND " : " OR ");

			var value = $"MV_SLICE({field}, {position}, {position})";

			// Past the last value MV_SLICE is null, and so would be the test, leaving
			// the whole predicate undefined instead of answering either way. An absent
			// value satisfies All and does not satisfy Any, so it is spelled out: the
			// result then stays definite under an enclosing NOT.
			_ = _builder.Append("COALESCE(");

			if (all)
				_ = _builder.Append(value).Append(" IS NULL OR ");

			AppendValuePredicate(value, isString, predicate.Kind, values);

			_ = _builder.Append(", ").Append(all ? "true" : "false").Append(')');
		}

		// closes the positions, then the CASE
		_ = _builder.Append("))");
		return true;
	}

	/// <summary>One value of a multi-value field, tested against the predicate.</summary>
	private void AppendValuePredicate(string value, bool isString, ElementPredicateKind kind, IReadOnlyList<string> values)
	{
		var text = isString ? value : $"TO_STRING({value})";

		switch (kind)
		{
			case ElementPredicateKind.StartsWith:
				_ = _builder.Append("STARTS_WITH(").Append(text).Append(", \"")
					.Append(EscapeStringLiteral(values[0])).Append("\")");
				break;

			case ElementPredicateKind.EndsWith:
				_ = _builder.Append("ENDS_WITH(").Append(text).Append(", \"")
					.Append(EscapeStringLiteral(values[0])).Append("\")");
				break;

			case ElementPredicateKind.Contains:
				_ = _builder.Append(text).Append(" LIKE \"*").Append(EscapeLikePattern(values[0])).Append("*\"");
				break;

			case ElementPredicateKind.In:
				_ = _builder.Append('(');

				for (var i = 0; i < values.Count; i++)
				{
					if (i > 0)
						_ = _builder.Append(" OR ");

					_ = _builder.Append(text).Append(" == \"").Append(EscapeStringLiteral(values[i])).Append('"');
				}

				_ = _builder.Append(')');
				break;

			default:
				_ = _builder.Append(text).Append(" == \"").Append(EscapeStringLiteral(values[0])).Append('"');
				break;
		}
	}

	/// <summary>Escapes what a double-quoted ES|QL string literal reserves.</summary>
	private static string EscapeStringLiteral(string value) =>
		value.Replace("\\", "\\\\").Replace("\"", "\\\"");

	private bool TryAppendMatch(Expression field, Expression value)
	{
		if (!TryGetConstant(value, out var constant))
			return false;

		AppendMatch(field.ResolveFieldName(_context.Metadata), constant);
		return true;
	}

	/// <summary>A collection-typed member of the document, as opposed to a captured constant.</summary>
	private static bool IsMultiValueField(Expression expression) =>
		IsEnumerableType(expression.Type) && ContainsParameter(expression);

	private static bool ContainsParameter(Expression expression) => expression switch
	{
		ParameterExpression => true,
		MemberExpression member => member.Expression is not null && ContainsParameter(member.Expression),
		UnaryExpression unary => ContainsParameter(unary.Operand),
		MethodCallExpression call => call.Object is not null && ContainsParameter(call.Object),
		_ => false
	};

	private static Expression StripQuotes(Expression expression)
	{
		var current = expression;

		while (current is UnaryExpression { NodeType: ExpressionType.Quote } quote)
			current = quote.Operand;

		return current;
	}

	private bool TryVisitCollectionContains(MethodCallExpression node)
	{
		if (TryGetContainsArguments(node, out var valueExpression, out var collection))
		{
			AppendContainsCollection(valueExpression, collection);
			return true;
		}

		return false;
	}

	private static bool TryGetContainsArguments(MethodCallExpression node, out Expression valueExpression, out IEnumerable? collection)
	{
		valueExpression = null!;
		collection = null;

		if (node.Method.IsStatic)
		{
			if (node.Method.DeclaringType == typeof(Enumerable) && node.Arguments.Count == 2)
			{
				valueExpression = node.Arguments[1];
				return TryGetCollectionValue(node.Arguments[0], out collection);
			}

			if (node.Method.DeclaringType == typeof(MemoryExtensions) && node.Arguments.Count == 2)
			{
				var source = TryUnwrapMemoryExtensionsSource(node.Arguments[0]);
				if (source is null)
					return false;

				valueExpression = node.Arguments[1];
				return TryGetCollectionValue(source, out collection);
			}

			return false;
		}

		if (node.Object is null || node.Arguments.Count != 1 || !IsEnumerableType(node.Object.Type))
			return false;

		valueExpression = node.Arguments[0];
		return TryGetCollectionValue(node.Object, out collection);
	}

	private static Expression? TryUnwrapMemoryExtensionsSource(Expression expression)
	{
		var current = expression;

		while (true)
		{
			// Handle implicit/explicit conversions (e.g., array -> ReadOnlySpan<T>)
			while (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
				current = unary.Operand;

			if (current is not MethodCallExpression methodCall || methodCall.Arguments.Count == 0)
				break;

			// Handle explicit AsSpan(...) wrappers emitted in expression trees.
			if (methodCall.Method.DeclaringType == typeof(MemoryExtensions) && methodCall.Method.Name == "AsSpan")
			{
				current = methodCall.Arguments[0];
				continue;
			}

			// Handle op_Implicit wrappers used for array -> ReadOnlySpan<T> conversions.
			if (methodCall.Method.Name == "op_Implicit")
			{
				current = methodCall.Arguments[0];
				continue;
			}

			break;
		}

		return IsEnumerableType(current.Type) ? current : null;
	}

	private static bool IsEnumerableType(Type type) =>
		type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

	private static bool TryGetCollectionValue(Expression expression, out IEnumerable? collection)
	{
		collection = null;

		if (!IsEnumerableType(expression.Type))
			return false;

		try
		{
			collection = GetConstantValue(expression) as IEnumerable;
			return true;
		}
		catch (NotSupportedException)
		{
			return false;
		}
	}

	private void AppendContainsCollection(Expression valueExpression, IEnumerable? collection)
	{
		// Enumerable/List Contains over an empty set is always false.
		if (collection is null)
			throw new ArgumentNullException(nameof(collection), "Collection used with Contains cannot be null.");

		var values = collection.Cast<object?>().ToList();
		if (values.Count == 0)
		{
			_ = _builder.Append("false");
			return;
		}

		_ = Visit(valueExpression);
		_ = _builder.Append(" IN (");

		for (var i = 0; i < values.Count; i++)
		{
			if (i > 0)
				_ = _builder.Append(", ");

			_ = _builder.Append(_context.FormatValue(values[i]));
		}

		_ = _builder.Append(')');
	}

	private static string GetOperator(ExpressionType nodeType) =>
		nodeType switch
		{
			ExpressionType.Equal => "==",
			ExpressionType.NotEqual => "!=",
			ExpressionType.LessThan => "<",
			ExpressionType.LessThanOrEqual => "<=",
			ExpressionType.GreaterThan => ">",
			ExpressionType.GreaterThanOrEqual => ">=",
			ExpressionType.AndAlso => "AND",
			ExpressionType.OrElse => "OR",
			ExpressionType.Add => "+",
			ExpressionType.Subtract => "-",
			ExpressionType.Multiply => "*",
			ExpressionType.Divide => "/",
			ExpressionType.Modulo => "%",
			_ => throw new NotSupportedException($"Operator {nodeType} is not supported.")
		};

	private static object? GetConstantValue(Expression expression)
	{
		try
		{
			return ExpressionConstantResolver.Resolve(expression);
		}
		catch (NotSupportedException ex)
		{
			throw new NotSupportedException($"Expression '{expression}' is not supported for constant evaluation.", ex);
		}
	}

	private static object? GetMemberValue(MemberExpression member, object? instance) =>
		member.Member switch
		{
			FieldInfo field => field.GetValue(instance),
			PropertyInfo property => property.GetValue(instance),
			_ => throw new NotSupportedException($"Member type {member.Member.GetType()} is not supported.")
		};

	private static object? GetStaticMemberValue(MemberExpression member) =>
		member.Member switch
		{
			FieldInfo field => field.GetValue(null),
			PropertyInfo property => property.GetValue(null),
			_ => throw new NotSupportedException($"Static member type {member.Member.GetType()} is not supported.")
		};

	/// <summary>A null literal, or a captured variable that holds null.</summary>
	private static bool ResolvesToNullConstant(Expression expression)
	{
		if (IsNullConstant(expression))
			return true;

		if (expression is not (MemberExpression or UnaryExpression { NodeType: ExpressionType.Convert }))
			return false;

		try
		{
			return GetConstantValue(expression) is null;
		}
		catch (NotSupportedException)
		{
			return false;
		}
	}

	private static bool IsNullConstant(Expression expression) =>
		expression is ConstantExpression { Value: null };

	private static string EscapeLikePattern(string value) =>
		// Escape special characters in LIKE patterns
		value
			.Replace("\\", "\\\\")
			.Replace("\"", "\\\"")
			.Replace("*", "\\*")
			.Replace("?", "\\?");
}
