// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Elastic.Esql.Extensions;
using Elastic.Esql.Formatting;
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

	// Values resolved by the IS NULL rewrite, keyed by the member node so VisitMember can reuse
	// them instead of evaluating the same closure chain (and its getters) a second time.
	private readonly Dictionary<Expression, object?> _resolvedCaptures = [];

	/// <summary>
	/// Translates a predicate expression to an ES|QL condition string.
	/// </summary>
	public string Translate(Expression expression)
	{
		_ = _builder.Clear();
		_resolvedCaptures.Clear();
		_ = Visit(expression);
		return _builder.ToString();
	}

	protected override Expression VisitBinary(BinaryExpression node)
	{
		if (TryVisitRootNullGuard(node))
			return node;

		if (TryVisitStringComparison(node))
			return node;

		if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
		{
			var nullOp = node.NodeType == ExpressionType.Equal ? "IS NULL" : "IS NOT NULL";

			if (ResolvesToNull(node.Right))
			{
				AppendComparisonOperand(node.Left, parentIsEquality: true);
				_ = _builder.Append(' ').Append(nullOp);
				return node;
			}

			if (ResolvesToNull(node.Left))
			{
				AppendComparisonOperand(node.Right, parentIsEquality: true);
				_ = _builder.Append(' ').Append(nullOp);
				return node;
			}
		}

		// Must run after null handling (IS NULL wins) and before generic enum comparison (which would emit C# ordinals).
		var dayOfWeekComparison = EsqlFunctionTranslator.TryGetDayOfWeekComparison(node);
		if (dayOfWeekComparison.HasValue)
		{
			_ = Visit(dayOfWeekComparison.Value.DateMember);
			_ = _builder.Append(' ').Append(EsqlFunctionTranslator.GetOperator(node.NodeType)).Append(' ').Append(dayOfWeekComparison.Value.IsoDayNumber);
			return node;
		}

		// Parenthesize logical and arithmetic nodes so the C# expression tree grouping survives;
		// a flat rendering would let ES|QL re-associate operands by its own precedence rules.
		var needsParentheses = node.NodeType
			is ExpressionType.AndAlso or ExpressionType.OrElse
			or ExpressionType.Add or ExpressionType.Subtract
			or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo;

		if (needsParentheses)
			_ = _builder.Append('(');

		var enumComparison = TryGetEnumComparison(node);
		if (enumComparison.HasValue && !IsSpecialEnumAccess(enumComparison.Value.MemberSide.Member))
		{
			var propertyMember = enumComparison.Value.MemberSide.Member;
			_ = Visit(enumComparison.Value.MemberSide);

			// The member side is always emitted first; when it originally sat on the right,
			// relational operators must be mirrored to preserve the predicate.
			var op = EsqlFunctionTranslator.GetOperator(enumComparison.Value.Swapped ? MirrorComparison(node.NodeType) : node.NodeType);
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
			var parentIsEquality = node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual;
			_comparisonPropertyContext = ExtractEntityPropertyMember(node);
			AppendComparisonOperand(node.Left, parentIsEquality);
			var op = EsqlFunctionTranslator.GetOperator(node.NodeType);
			_ = _builder.Append(' ').Append(op).Append(' ');
			AppendComparisonOperand(node.Right, parentIsEquality);
			_comparisonPropertyContext = null;
		}

		if (needsParentheses)
			_ = _builder.Append(')');

		return node;
	}

	/// <summary>
	/// A comparison nested as an equality operand must keep its own parentheses;
	/// ES|QL misparses the flat form (e.g. <c>a > b == flag</c>).
	/// </summary>
	private void AppendComparisonOperand(Expression operand, bool parentIsEquality)
	{
		var isNestedComparison = parentIsEquality && operand.UnwrapConvertExpressions() is BinaryExpression
		{
			NodeType: ExpressionType.Equal or ExpressionType.NotEqual
				or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
				or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
		};

		if (isNestedComparison)
			_ = _builder.Append('(');

		_ = Visit(operand);

		if (isNestedComparison)
			_ = _builder.Append(')');
	}

	/// <summary>
	/// Inspects a <see cref="BinaryExpression"/> and determines whether it represents an enum comparison. If so, returns the enum type, the member site
	/// expression (the property/field being compared), the constant enum value, and whether the member side originally sat on the right-hand side.
	/// Handles both regular and nullable enums.
	/// </summary>
	private static (Type EnumType, MemberExpression MemberSide, Expression ConstantSide, bool Swapped)? TryGetEnumComparison(BinaryExpression binary)
	{
		// TODO: We can probably make this more robust by explicitly looking for the parametrized member access as the source of truth for the enum type.

		if (binary.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual
			or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
			or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual))
			return null;

		// Try both orientations: member == constant and constant == member.
		return TryMatch(binary.Left, binary.Right, swapped: false) ?? TryMatch(binary.Right, binary.Left, swapped: true);

		static (Type EnumType, MemberExpression MemberSide, Expression ConstantSide, bool Swapped)? TryMatch(
			Expression candidateMember,
			Expression candidateConstant,
			bool swapped
		)
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

			return (enumType, memberExpression, constantSide, swapped);
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

		return EntityPropertyMember(node.Left) ?? EntityPropertyMember(node.Right);
	}

	private static MemberInfo? EntityPropertyMember(Expression expr)
	{
		var unwrapped = expr.UnwrapConvertExpressions();
		if (unwrapped is MemberExpression member && ExpressionTranslationHelpers.IsRootedInParameter(member))
			return member.Member;

		return null;
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
		// Closure-rooted member paths (captured variables and member chains of any depth on
		// captured objects) resolve to a constant value and emit as a parameter or inline literal.
		if (node.Expression.IsClosureRooted())
		{
			if (!_resolvedCaptures.TryGetValue(node, out var value))
				value = ExpressionConstantResolver.Resolve(node);
			else
				_ = _resolvedCaptures.Remove(node);
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
				var translated = EsqlFunctionTranslator.TryTranslateStaticDateProperty(memberName);
				if (translated is not null)
				{
					_ = _builder.Append(translated);
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
			var translated = EsqlFunctionTranslator.TryTranslateDateMember(node.Member.Name, dateExpr);
			if (translated != null)
			{
				_ = _builder.Append(translated);
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
		return EsqlFunctionTranslator.TryTranslateStaticDateProperty(memberName)
			?? throw new NotSupportedException($"DateTime static property {memberName} is not supported.");
	}

	private string TranslateEsqlFunctionForDateTime(MethodCallExpression methodCall)
	{
		var methodName = methodCall.Method.Name;
		var translated = EsqlFunctionTranslator.TryTranslateMethodCall(methodCall, TranslateDateTimeExpression);
		return translated ?? throw new NotSupportedException($"EsqlFunction {methodName} is not supported in DateTime context.");
	}

	protected override Expression VisitNew(NewExpression node)
	{
		// Fold inline constructor calls (e.g. new DateTime(2024, 1, 1)) to their value; the base
		// visitor would render each ctor argument individually, concatenating a corrupt literal.
		var value = ExpressionConstantResolver.Resolve(node);
		_ = _builder.Append(_context.FormatValue(value, _comparisonPropertyContext));
		_comparisonPropertyContext = null;
		return node;
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
		{
			// IsNull and IsNotNull take the field to test, and the row is not one: it has
			// no field name to put in front of the operator, so the marker would emit the
			// operator with nothing before it.
			if (methodName is nameof(EsqlFunctions.IsNull) or nameof(EsqlFunctions.IsNotNull)
				&& node.Arguments is [{ } only]
				&& only.UnwrapConvertExpressions() is ParameterExpression)
			{
				throw new NotSupportedException(
					$"EsqlFunctions.{methodName} on the row itself is not supported: pass the field "
					+ "to test, since the row has no field name of its own to put in front of the operator.");
			}

			return VisitEsqlFunction(node);
		}

		// String methods
		if (declaringType == typeof(string))
			return VisitStringMethod(node);

		// Math methods
		if (declaringType == typeof(Math))
			return VisitMathMethod(node);

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
		var count = Convert.ToDouble(GetConstantValue(node.Arguments[0]), CultureInfo.InvariantCulture);

		var (interval, unit) = methodName switch
		{
			"FromDays" => (TimeSpan.FromDays(count), "days"),
			"FromHours" => (TimeSpan.FromHours(count), "hours"),
			"FromMinutes" => (TimeSpan.FromMinutes(count), "minutes"),
			"FromSeconds" => (TimeSpan.FromSeconds(count), "seconds"),
			"FromMilliseconds" => (TimeSpan.FromMilliseconds(count), "milliseconds"),
			_ => throw new NotSupportedException($"TimeSpan method {methodName} is not supported.")
		};

		// ES|QL duration counts are integers. An integral argument keeps the unit the caller wrote; a
		// fractional one is re-expressed in whole milliseconds or rejected, exactly like a captured TimeSpan.
		var literal = Math.Floor(count) == count
			? string.Format(CultureInfo.InvariantCulture, "{0} {1}", count, unit)
			: EsqlFormatting.FormatTimeSpanRaw(interval);
		_ = _builder.Append(literal);

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
		// Translate into the builder tail and truncate afterwards, so nested
		// arguments never re-copy the already accumulated condition prefix.
		var start = _builder.Length;
		_ = Visit(expression);
		var result = _builder.ToString(start, _builder.Length - start);
		_builder.Length = start;
		return result;
	}

	private Expression VisitStringMethod(MethodCallExpression node)
	{
		var methodName = node.Method.Name;

		EsqlFunctionTranslator.ThrowIfUnsupportedStringComparison(node);

		switch (methodName)
		{
			case "Contains":
				// string.Contains("x") → LIKE "*x*"
				_ = Visit(node.Object);
				_ = _builder.Append(" LIKE ");
				var containsValue = RequireSearchValue(node, methodName);
				_ = _builder.Append(EsqlFormatting.FormatString($"*{EscapeLikePattern(containsValue)}*"));
				break;

			case "StartsWith":
				// string.StartsWith("x") → LIKE "x*"
				_ = Visit(node.Object);
				_ = _builder.Append(" LIKE ");
				var startsValue = RequireSearchValue(node, methodName);
				_ = _builder.Append(EsqlFormatting.FormatString($"{EscapeLikePattern(startsValue)}*"));
				break;

			case "EndsWith":
				// string.EndsWith("x") → LIKE "*x"
				_ = Visit(node.Object);
				_ = _builder.Append(" LIKE ");
				var endsValue = RequireSearchValue(node, methodName);
				_ = _builder.Append(EsqlFormatting.FormatString($"*{EscapeLikePattern(endsValue)}"));
				break;

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
				var indexerTarget = node.Object ?? throw new NotSupportedException("The string indexer requires an instance.");
				var indexer = EsqlFunctionTranslator.TranslateStringIndexer(TranslateSubExpression(indexerTarget), node.Arguments[0], TranslateSubExpression);
				_ = _builder.Append(indexer);
				break;

			case "CompareTo":
			case "Compare":
			case "CompareOrdinal":
				// an ordering only exists inside a comparison against zero, which the binary
				// visitor rewrites into a direct comparison, and only for the ordinal forms
				throw new NotSupportedException(
					$"String method {methodName} is only supported as string.CompareOrdinal(a, b) "
					+ "or string.Compare(a, b, StringComparison.Ordinal) inside an ordering "
					+ "comparison against zero, for example string.CompareOrdinal(a, b) > 0.");

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
			if (node.Method.DeclaringType == typeof(Enumerable) && node.Arguments.Count >= 2)
			{
				valueExpression = node.Arguments[1];
				return TryGetCollectionValue(node.Arguments[0], out collection);
			}

			if (node.Method.DeclaringType == typeof(MemoryExtensions) && node.Arguments.Count >= 2)
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

	/// <summary>Mirrors a relational operator for a comparison whose operands were swapped into member-first order.</summary>
	private static ExpressionType MirrorComparison(ExpressionType nodeType) =>
		nodeType switch
		{
			ExpressionType.LessThan => ExpressionType.GreaterThan,
			ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
			ExpressionType.GreaterThan => ExpressionType.LessThan,
			ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
			_ => nodeType
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

	private static object? GetStaticMemberValue(MemberExpression member) =>
		member.Member switch
		{
			FieldInfo field => field.GetValue(null),
			PropertyInfo property => property.GetValue(null),
			_ => throw new NotSupportedException($"Static member type {member.Member.GetType()} is not supported.")
		};

	private static bool IsNullConstant(Expression expression) =>
		expression is ConstantExpression { Value: null };

	/// <summary>
	/// True when the operand is a syntactic null or a closure/static-rooted expression whose
	/// runtime value is null. Rendering such a value inline would emit a dead <c>== null</c>
	/// comparison (always null in ES|QL) instead of the intended <c>IS NULL</c>.
	/// </summary>
	private bool ResolvesToNull(Expression expression)
	{
		if (IsNullConstant(expression))
			return true;

		if (expression is ConstantExpression || !expression.SupportsEvaluation())
			return false;

		var target = expression.UnwrapConvertExpressions();

		// A closure-rooted chain runs exactly once: a failure here is the failure VisitMember would
		// raise anyway, so it propagates instead of re-running the chain's getters.
		if (target.IsClosureRooted())
		{
			var value = ExpressionConstantResolver.Resolve(target);
			_resolvedCaptures[target] = value;
			return value is null;
		}

		try
		{
			return ExpressionConstantResolver.Resolve(target) is null;
		}
		catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or TargetInvocationException)
		{
			// Static markers such as EsqlMetadata throw on evaluation and keep their dedicated
			// translation. Anything else is a real bug: propagate.
			return false;
		}
	}

	// C# throws for a null search value; rendering it as an empty pattern would silently turn the
	// predicate into a match-all LIKE.
	private static string RequireSearchValue(MethodCallExpression node, string methodName) =>
		GetConstantValue(node.Arguments[0])?.ToString()
			?? throw new NotSupportedException($"The search value passed to '{methodName}' must not be null; the LIKE pattern would match everything.");

	/// <summary>
	/// "p != null" on the lambda parameter itself: the document is never null, and
	/// there is no field to put in front of IS NOT NULL, so the guard is a constant.
	/// </summary>
	private bool TryVisitRootNullGuard(BinaryExpression node)
	{
		if (node.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
			return false;

		// ResolvesToNull reads the captured form too, and the parameter side is unwrapped,
		// since a hand-built tree converts the row to object to match the operand types.
		var parameter = node.Left.UnwrapConvertExpressions() is ParameterExpression left && ResolvesToNull(node.Right) ? left
			: node.Right.UnwrapConvertExpressions() is ParameterExpression right && ResolvesToNull(node.Left) ? right
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

		_ = _builder.Append(node.NodeType == ExpressionType.Equal ? "false" : "true");
		return true;
	}

	/// <summary>
	/// Whether the parameter stands for the document row rather than a projected value.
	/// Matching the element type is not enough: a recursive type projects to itself, as in
	/// ".Select(n => n.Child)" over "Node.Child : Node?", and the projected value may well
	/// be null. Keep and Drop narrow the columns but leave the row, so the question is
	/// whether a Select has run, not which commands were emitted. The element type is a
	/// document type and is set before any Where runs, so it alone decides the first half.
	/// </summary>
	private bool IsDocumentParameter(ParameterExpression parameter) =>
		parameter.Type.IsAssignableFrom(_context.ElementType)
		&& !_context.HasProjected;

	private static string EscapeLikePattern(string value) =>
		// Pattern-level escaping only: a backslash escapes LIKE wildcards. String-literal
		// escaping (quotes, backslashes) is applied afterwards by EsqlFormatting.FormatString.
		value
			.Replace("\\", "\\\\")
			.Replace("*", "\\*")
			.Replace("?", "\\?");

	/// <summary>
	/// Rewrites <c>string.CompareOrdinal(a, b) &gt; 0</c>, or
	/// <c>string.Compare(a, b, StringComparison.Ordinal) &gt; 0</c>, into <c>a &gt; b</c>.
	/// Only comparisons against the constant zero carry an ordering, and only the
	/// explicitly ordinal forms are accepted: <c>CompareTo</c> and the two-argument
	/// <c>Compare</c> order by the current culture, which is not something ES|QL can be
	/// asked for, so they are refused with a pointer to the ordinal forms.
	/// <para>
	/// Even the ordinal forms are not identical to what Elasticsearch does: .NET compares
	/// UTF-16 code units, Elasticsearch the UTF-8 bytes of a keyword, and the two orders
	/// disagree on exactly one kind of pair, a supplementary character against a character
	/// in U+E000 to U+FFFF. A comparison is decided by the first character that differs,
	/// so when the value compared against holds neither a supplementary character nor
	/// one at or above U+E000, no such pair can arise, whatever the field holds, and the
	/// translation is exact. Only that case is translated; a value outside it, or two
	/// fields compared with each other, is refused rather than ordered wrongly.
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

		// From here the shape is the supported one, so anything refused is refused with
		// its own reason rather than the generic "only when compared to zero" message.
		var (first, second) = OrdinalOperands(call);
		EnsureOrderingIsExact(call.Method.Name, first, second);

		var op = node.NodeType switch
		{
			ExpressionType.GreaterThan => flipped ? "<" : ">",
			ExpressionType.GreaterThanOrEqual => flipped ? "<=" : ">=",
			ExpressionType.LessThan => flipped ? ">" : "<",
			_ => flipped ? ">=" : "<="
		};

		AppendOrderingComparison(call.Method.Name, first, second, op);
		return true;
	}

	/// <summary>
	/// The two operands of an ordinal comparison. Any other overload orders by the current
	/// culture, an ignore-case flag, a range or a non-string operand, and is refused.
	/// </summary>
	private static (Expression First, Expression Second) OrdinalOperands(MethodCallExpression call)
	{
		var parameters = call.Method.GetParameters();
		var ordinalForm = call.Object is null
			&& parameters.Length is 2 or 3
			&& parameters[0].ParameterType == typeof(string)
			&& parameters[1].ParameterType == typeof(string)
			&& (call.Method.Name == "CompareOrdinal"
				? parameters.Length == 2
				: parameters.Length == 3 && parameters[2].ParameterType == typeof(StringComparison));

		if (!ordinalForm)
		{
			throw new NotSupportedException(
				$"String method {call.Method.Name} is only supported as string.CompareOrdinal(a, b) or "
				+ "string.Compare(a, b, StringComparison.Ordinal): every other overload orders by the "
				+ "current culture, an ignore-case flag, a range or a non-string operand, none of "
				+ "which is the UTF-8 byte ordering ES|QL applies to a keyword field.");
		}

		if (parameters.Length == 3 && !IsOrdinalComparison(call.Arguments[2]))
		{
			throw new NotSupportedException(
				$"String method {call.Method.Name} with a StringComparison argument other than "
				+ "StringComparison.Ordinal is not supported: keyword values are ordered by their "
				+ "UTF-8 bytes and comparison is case-sensitive, so any other comparison mode asks "
				+ "for an ordering Elasticsearch does not apply.");
		}

		return (call.Arguments[0], call.Arguments[1]);
	}

	/// <summary>
	/// Whether the StringComparison argument asks for the ordering ES|QL performs.
	/// Only <see cref="StringComparison.Ordinal"/> does: keyword ordering is
	/// case-sensitive, so OrdinalIgnoreCase would order "a" and "B" the other way.
	/// <para>
	/// This is the ordering comparison, which a binary node carries, so it is checked
	/// here rather than where a string method call is visited: the comparison against
	/// zero is folded away before the call itself is reached.
	/// </para>
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
	/// Whether the operand is null: the null literal, or a captured variable holding it.
	/// A field is not one, since its value is not known here.
	/// </summary>
	private static bool IsNullOperand(Expression expression)
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

	/// <summary>
	/// Refuses the operands whose ordering the translation cannot reproduce: a null
	/// operand, two fields with no value to look at, and a value holding a character on
	/// which the UTF-16 and UTF-8 orderings can disagree.
	/// </summary>
	private static void EnsureOrderingIsExact(string methodName, Expression first, Expression second)
	{
		// .NET orders a non-null string above null, which a plain ES|QL comparison
		// against null does not reproduce; there is a field to test for null instead
		if (IsNullOperand(first) || IsNullOperand(second))
		{
			throw new NotSupportedException(
				$"String method {methodName} against null is not supported: compare the "
				+ "field with null directly, which ES|QL answers with IS NULL.");
		}

		// The UTF-16 and UTF-8 orders disagree only between a supplementary character
		// and one in U+E000 to U+FFFF. With a value holding neither, the first differing
		// character can never be such a pair, so the translation is exact for any field.
		var value = TryGetConstant(first, out var firstValue) ? firstValue
			: TryGetConstant(second, out var secondValue) ? secondValue
			: null;

		if (value is not string text)
		{
			throw new NotSupportedException(
				$"String method {methodName} between two fields is not supported: without a "
				+ "value to look at, the translation cannot tell whether the UTF-16 ordering of .NET "
				+ "and the UTF-8 ordering of Elasticsearch agree on the comparison.");
		}

		if (text.Any(character => char.IsSurrogate(character) || character >= '\uE000'))
		{
			throw new NotSupportedException(
				$"String method {methodName} against a value holding a character at or above "
				+ "U+E000, or outside the Basic Multilingual Plane, is not supported: on such a value "
				+ "the UTF-16 ordering of .NET and the UTF-8 ordering of Elasticsearch can disagree, "
				+ "so the comparison is left untranslated rather than answered with the wrong order.");
		}
	}

	/// <summary>
	/// Emits the comparison with the ordering of a missing operand spelled out: .NET
	/// orders null before every string, where a comparison against a missing field is
	/// null in ES|QL and drops the row. One side is a value by now, so at most one side
	/// can be missing, and its ordering can be spelled out for the field itself, not for
	/// an expression of it, whose value for a missing field is not the field's null.
	/// </summary>
	private void AppendOrderingComparison(string methodName, Expression first, Expression second, string op)
	{
		var firstMayBeMissing = AsNullableField(first);
		var secondMayBeMissing = AsNullableField(second);

		if ((firstMayBeMissing is null && ReadsANullableField(first)) || (secondMayBeMissing is null && ReadsANullableField(second)))
		{
			throw new NotSupportedException(
				$"String method {methodName} over an expression of a field that can be missing is "
				+ "not supported: the ordering of a missing value can be spelled out for the field "
				+ "itself, not for an expression of it. Compare the field directly.");
		}

		var descending = op[0] == '>';
		var guardedField = firstMayBeMissing ?? secondMayBeMissing;
		var guardClause = firstMayBeMissing is not null
			// a missing left operand sorts first: below anything, never above
			? descending ? " IS NOT NULL AND " : " IS NULL OR "
			// a missing right operand sorts first: anything is above it, nothing below
			: descending ? " IS NULL OR " : " IS NOT NULL AND ";

		if (guardedField is not null)
			_ = _builder.Append('(').Append(guardedField).Append(guardClause);

		// as for a relational operator: the value is serialized through the converter of the property it is compared with
		_comparisonPropertyContext = EntityPropertyMember(first) ?? EntityPropertyMember(second);
		_ = Visit(first);
		_ = _builder.Append(' ').Append(op).Append(' ');
		_ = Visit(second);
		_comparisonPropertyContext = null;

		if (guardedField is not null)
			_ = _builder.Append(')');
	}

	/// <summary>
	/// The emitted path of an operand that can be missing: a member path declared nullable
	/// at any step, or a multi-field of one. A missing parent leaves the whole path null,
	/// so the guard goes on the path as emitted, whatever the last member says.
	/// </summary>
	private string? AsNullableField(Expression expression) => expression switch
	{
		MemberExpression member when IsNullableFieldPath(member) => ResolveFieldPath(member),
		MethodCallExpression { Method.Name: "MultiField", Arguments: [MemberExpression member, ConstantExpression { Value: string }] } call
			when call.Method.DeclaringType == typeof(GeneralPurposeExtensions) && IsNullableFieldPath(member)
			=> call.ResolveFieldName(_context.Metadata),
		_ => null
	};

	/// <summary>
	/// Whether the member path is rooted in the parameter and can be missing: "l.Host.Name"
	/// is missing whenever Host is, so any nullable member along the path counts.
	/// </summary>
	private static bool IsNullableFieldPath(MemberExpression member)
	{
		if (!ExpressionTranslationHelpers.IsRootedInParameter(member))
			return false;

		for (Expression? current = member; current is MemberExpression step; current = step.Expression)
		{
			if (IsDeclaredNullable(step.Member))
				return true;
		}

		return false;
	}

	/// <summary>Whether any member path read anywhere in the expression can be missing.</summary>
	private static bool ReadsANullableField(Expression expression)
	{
		var finder = new NullableFieldFinder();
		_ = finder.Visit(expression);
		return finder.Found;
	}

	private sealed class NullableFieldFinder : ExpressionVisitor
	{
		public bool Found { get; private set; }

		protected override Expression VisitMember(MemberExpression node)
		{
			if (IsNullableFieldPath(node))
				Found = true;

			return base.VisitMember(node);
		}
	}

	/// <summary>
	/// Whether the member is declared nullable: a <c>Nullable&lt;T&gt;</c>, or a reference
	/// annotated as nullable. Only then is the guard worth writing: on a non-nullable
	/// member the comparison already reads the way the source does.
	/// <para>
	/// The compiler records the annotation as a <c>NullableAttribute</c> on the member, or
	/// omits it and records a <c>NullableContextAttribute</c> on the declaring type when
	/// every member shares the same annotation. Both are read from the attribute data
	/// rather than by instantiating the attribute, which keeps the check out of the
	/// trimmer's way.
	/// </para>
	/// </summary>
	internal static bool IsDeclaredNullable(MemberInfo member)
	{
		var type = member switch
		{
			PropertyInfo property => property.PropertyType,
			FieldInfo field => field.FieldType,
			_ => null
		};

		if (type is null)
			return false;

		if (Nullable.GetUnderlyingType(type) is not null)
			return true;

		if (type.IsValueType)
			return false;

		var own = NullableFlag(member.GetCustomAttributesData(), "System.Runtime.CompilerServices.NullableAttribute");

		if (own is not null)
			return own == 2;

		for (var declaring = member.DeclaringType; declaring is not null; declaring = declaring.DeclaringType)
		{
			var context = NullableFlag(declaring.GetCustomAttributesData(), "System.Runtime.CompilerServices.NullableContextAttribute");

			if (context is not null)
				return context == 2;
		}

		return false;
	}

	/// <summary>
	/// The first nullability flag carried by the named attribute: 2 for annotated
	/// (nullable), 1 for not annotated, 0 for oblivious. The constructor takes either one
	/// byte or an array whose first element describes the outermost type.
	/// </summary>
	private static byte? NullableFlag(IEnumerable<CustomAttributeData> attributes, string attributeName)
	{
		var data = attributes.FirstOrDefault(attribute => attribute.AttributeType.FullName == attributeName);

		if (data is null || data.ConstructorArguments.Count == 0)
			return null;

		return data.ConstructorArguments[0].Value switch
		{
			byte flag => flag,
			IReadOnlyCollection<CustomAttributeTypedArgument> { Count: > 0 } flags => flags.First().Value as byte?,
			_ => null
		};
	}

	/// <summary>A constant the expression evaluates to, when it has one that is not null.</summary>
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
}
