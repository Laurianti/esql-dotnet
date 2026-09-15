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
	/// Rewrites <c>a.CompareTo(b) &gt; 0</c> or <c>string.Compare(a, b) &gt; 0</c> into
	/// <c>a &gt; b</c>. Only comparisons against the constant zero are handled, which is
	/// the only shape that carries an ordering meaning.
	/// </summary>
	/// <summary>
	/// "p != null" on the lambda parameter itself: the document is never null, and
	/// there is no field to put in front of IS NOT NULL, so the guard is a constant.
	/// </summary>
	private bool TryVisitRootNullGuard(BinaryExpression node)
	{
		if (node.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
			return false;

		var isRootGuard = (node.Left is ParameterExpression && IsNullConstant(node.Right))
			|| (node.Right is ParameterExpression && IsNullConstant(node.Left));

		if (!isRootGuard)
			return false;

		_ = _builder.Append(node.NodeType == ExpressionType.Equal ? "FALSE" : "TRUE");
		return true;
	}

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

		if (call.Method.DeclaringType != typeof(string) || call.Method.Name is not ("CompareTo" or "Compare"))
			return false;

		// Only the plain string overloads carry the ordering ES|QL performs. Anything
		// else, such as a StringComparison or a culture, asks for a comparison the
		// translation cannot honour, so it is left unsupported rather than ignored.
		var parameters = call.Method.GetParameters();

		if (parameters.Any(parameter => parameter.ParameterType != typeof(string)))
			return false;

		// instance form: a.CompareTo(b); static form: string.Compare(a, b)
		var (first, second) = call.Object is not null
			? parameters.Length == 1 ? (call.Object, call.Arguments[0]) : (null, null)
			: parameters.Length == 2 ? (call.Arguments[0], call.Arguments[1]) : (null, null);

		if (first is null || second is null)
			return false;

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
	/// Translates predicates over a multi-value document field into MATCH, which
	/// matches a document when any of the field's values matches, without the row
	/// duplication that MV_EXPAND would introduce.
	/// <para>
	/// Supported shapes: <c>field.Any(x =&gt; x == value)</c>, <c>field.Any()</c>,
	/// <c>field.Contains(value)</c>.
	/// </para>
	/// </summary>
	/// <summary>
	/// Separates the values of a multi-value field when they are joined into one
	/// string for a regular expression: the ASCII unit separator, which is reserved
	/// for exactly this and does not occur in data.
	/// </summary>
	private const string ValueSeparator = "\u001F";

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
	/// Predicates over multi-value document fields: field.Any(...), field.All(...) and
	/// field.Contains(value). A document holds every value of the field at once, so
	/// the quantifier is answered on the field itself, without expanding rows.
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
					ElementPredicateKind? kind = call.Method.Name switch
					{
						"StartsWith" => ElementPredicateKind.StartsWith,
						"EndsWith" => ElementPredicateKind.EndsWith,
						"Contains" => ElementPredicateKind.Contains,
						_ => null
					};

					if (kind is null || !TryGetConstant(call.Arguments[0], out var constant))
						return null;

					return new ElementPredicate(kind.Value, [constant], negated);
				}

				// values.Contains(x), over a constant collection
				if (TryGetContainsArguments(call, out var valueExpression, out var collection)
					&& valueExpression == element
					&& collection is not null)
				{
					return new ElementPredicate(ElementPredicateKind.In, collection.Cast<object?>().ToList(), negated);
				}

				return null;
			}

			default:
				return null;
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
		// satisfies the All that was written, so its guard is emitted around the
		// negation rather than inside it
		var vacuouslyTrue = all;
		var guardsMissingField = vacuouslyTrue
			&& predicate.Kind is ElementPredicateKind.GreaterThan or ElementPredicateKind.GreaterThanOrEqual
				or ElementPredicateKind.LessThan or ElementPredicateKind.LessThanOrEqual;

		if (guardsMissingField && predicate.Negated)
			_ = _builder.Append('(').Append(name).Append(" IS NULL OR ");

		var negated = predicate.Negated;

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

				// All() over a missing field holds; when a negation flipped the
				// quantifier the guard was already emitted around it
				var guardHere = vacuouslyTrue && !negated;

				if (guardHere)
					_ = _builder.Append('(').Append(name).Append(" IS NULL OR ");

				_ = _builder.Append(aggregate).Append('(').Append(name).Append(") ").Append(op).Append(' ')
					.Append(_context.FormatValue(predicate.Values[0], null));

				if (guardHere)
					_ = _builder.Append(')');

				if (guardsMissingField && negated)
					_ = _builder.Append(')');

				return true;
			}

			default:
				return TryAppendValuePattern(name, elementType, all, predicate);
		}
	}

	private void AppendMatch(string field, object? value) =>
		_ = _builder.Append("MATCH(").Append(field).Append(", ").Append(_context.FormatValue(value, null)).Append(')');

	/// <summary>
	/// Predicates that MATCH cannot answer, such as "starts with": the values are joined
	/// into one string, "␟a␟b␟", and a regular expression checks either that some value
	/// matches (Any) or that the string is made only of matching values (All).
	/// </summary>
	private bool TryAppendValuePattern(string field, Type elementType, bool all, ElementPredicate predicate)
	{
		var isString = elementType == typeof(string);
		var isIntegral = elementType == typeof(int) || elementType == typeof(long)
			|| elementType == typeof(short) || elementType == typeof(byte);

		// text predicates need text; equality over the joined string needs a value whose
		// text form is unambiguous, which rules out floating-point fields
		if (!isString && (!isIntegral || predicate.Kind is not (ElementPredicateKind.Equal or ElementPredicateKind.In)))
			return false;

		var values = predicate.Values.Select(v => v?.ToString() ?? "").ToList();

		foreach (var value in values)
		{
			if (value.Contains(ValueSeparator, StringComparison.Ordinal) || value.Contains("\"\"\"", StringComparison.Ordinal))
			{
				throw new NotSupportedException(
					"A value used in a predicate over a multi-value field cannot contain the "
					+ "unit separator (U+001F) or three consecutive double quotes.");
			}
		}

		var anyValue = "[^" + ValueSeparator + "]*";
		var valuePattern = predicate.Kind switch
		{
			ElementPredicateKind.Equal => EscapeRegex(values[0]),
			ElementPredicateKind.In => "(" + string.Join("|", values.Select(EscapeRegex)) + ")",
			ElementPredicateKind.StartsWith => EscapeRegex(values[0]) + anyValue,
			ElementPredicateKind.EndsWith => anyValue + EscapeRegex(values[0]),
			ElementPredicateKind.Contains => anyValue + EscapeRegex(values[0]) + anyValue,
			_ => throw new InvalidOperationException()
		};

		// All over an empty list holds only for the empty field; Any never does
		if (predicate.Kind == ElementPredicateKind.In && values.Count == 0)
		{
			_ = _builder.Append(all ? field + " IS NULL" : "FALSE");
			return true;
		}

		// Any: some value, between two separators, matches.
		// All: the whole string is a run of matching values; the lone separator, which
		// stands for the empty field, is one of those runs too.
		var pattern = all
			? "(" + ValueSeparator + valuePattern + ")*" + ValueSeparator
			: ".*" + ValueSeparator + valuePattern + ValueSeparator + ".*";

		var text = isString ? field : "TO_STRING(" + field + ")";
		var joined = $"COALESCE(CONCAT(\"{ValueSeparator}\", MV_CONCAT({text}, \"{ValueSeparator}\"), \"{ValueSeparator}\"), \"{ValueSeparator}\")";

		// A stored value may hold the separator itself, which would split it into two
		// synthetic values and match a pattern the real value does not. The joined
		// string carries one separator per value plus one, so any excess gives the
		// collision away, and such a document is left out rather than answered wrongly.
		var separators = $"(LENGTH({joined}) - LENGTH(REPLACE({joined}, \"{ValueSeparator}\", \"\")))";

		_ = _builder
			.Append('(').Append(separators)
			.Append(" == COALESCE(MV_COUNT(").Append(field).Append("), 0) + 1 AND ")
			.Append(joined).Append(" RLIKE \"\"\"").Append(pattern).Append("\"\"\")");

		return true;
	}

	/// <summary>Escapes the characters Lucene regular expressions reserve.</summary>
	private static string EscapeRegex(string value)
	{
		const string reserved = "\\.?+*|{}[]()\"#@&<>~";
		var builder = new StringBuilder(value.Length);

		foreach (var c in value)
		{
			if (reserved.Contains(c))
				_ = builder.Append('\\');

			_ = builder.Append(c);
		}

		return builder.ToString();
	}

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
