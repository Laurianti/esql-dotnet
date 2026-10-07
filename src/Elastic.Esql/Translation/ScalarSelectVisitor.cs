// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Elastic.Esql.Core;
using Elastic.Esql.Extensions;

namespace Elastic.Esql.Translation;

/// <summary>
/// Rewrites the operators that follow a Select returning a single value, such as
/// <c>Select(l => l.Duration * 3)</c> or <c>Select(l => l.Message)</c>, so that they read the column
/// the Select leaves: the field itself, or <c>result</c> for a computed value.
/// </summary>
internal sealed class ScalarSelectVisitor : ExpressionVisitor
{
	private static readonly HashSet<string> RowPreservingOperators =
	[
		nameof(Queryable.Where),
		nameof(Queryable.OrderBy),
		nameof(Queryable.OrderByDescending),
		nameof(Queryable.ThenBy),
		nameof(Queryable.ThenByDescending),
		nameof(Queryable.Take)
	];

	private static readonly HashSet<string> SelectorAggregates =
	[
		nameof(Queryable.Sum),
		nameof(Queryable.Average),
		nameof(Queryable.Min),
		nameof(Queryable.Max)
	];

	// The operators the translation supports over a row, whose lambdas take that row: after a single value they are
	// rewritten to read its column. Any other operator, such as Join, GroupJoin, SelectMany or TakeWhile, is left to
	// the translation, which refuses it with a message of its own.
	private static readonly HashSet<string> TranslatedOperators =
	[
		.. RowPreservingOperators,
		.. SelectorAggregates,
		nameof(Queryable.Select),
		nameof(Queryable.GroupBy),
		nameof(Queryable.First),
		nameof(Queryable.FirstOrDefault),
		nameof(Queryable.Single),
		nameof(Queryable.SingleOrDefault),
		nameof(Queryable.Count),
		nameof(Queryable.LongCount),
		nameof(Queryable.Any)
	];

	private static readonly ConcurrentDictionary<Type, PropertyInfo> ResultMembers = new();

	// The expression that stands for the single value of each row after a scalar Select, by the call that produces those rows.
	private readonly Dictionary<Expression, SingleValue> _singleValues = [];

	// The single aggregations that follow a GroupBy.
	private readonly HashSet<Expression> _groupedAggregations = [];

	private readonly record struct SingleValue(LambdaExpression Selector, Expression Column);

	/// <summary>
	/// The column the rows of <paramref name="source"/> hold their single value in, when they hold one: the field a
	/// Select reads, or the result column it computes. Sum(), Max() and the other aggregates without a selector name it.
	/// </summary>
	public Expression? SingleValueColumnOf(Expression source) =>
		_singleValues.TryGetValue(source, out var value) ? value.Column : null;

	/// <summary>
	/// Refuses an operator that reads the single value of rows whose column is not known, such as the rows of a RawEsql
	/// or a LookupJoin to a single-value type, or those after a Completion or a Fork: its operand would name no column.
	/// The translation calls this once it has translated the source, so that a refusal of an operator before comes first.
	/// </summary>
	public void ThrowIfReadingAValueWithoutColumn(MethodCallExpression node)
	{
		if (node.Method.DeclaringType != typeof(Queryable) || node.Arguments.Count == 0 || !TranslatedOperators.Contains(node.Method.Name))
			return;

		var source = node.Arguments[0];
		if (!_singleValues.ContainsKey(source) && HoldsASingleValue(source) && ReadsTheValue(node))
		{
			throw new NotSupportedException(
				$"{node.Method.Name} cannot read the single value of these rows, whose column is not known after the operator "
				+ "that produced them: project the value into a member, as in 'Select(l => new { Value = ... })', and read the member."
			);
		}
	}

	/// <summary>A selector returning a single value: a field, or a value computed from the row.</summary>
	public static bool IsScalarSelector(LambdaExpression lambda) =>
		lambda.Parameters.Count == 1
		&& TypeHelper.IsSingleValueType(lambda.ReturnType)
		&& lambda.Body is not NewExpression and not MemberInitExpression
		&& lambda.Body.UnwrapConvertExpressions() != lambda.Parameters[0];

	/// <summary>A single-value selector that computes its value rather than reading a field.</summary>
	public static bool IsComputed(LambdaExpression singleValueSelector) => !IsFieldPath(singleValueSelector.Body);

	/// <summary>
	/// The <c>Result</c> member of <c>ScalarRow&lt;T&gt;</c> for a value type, the column a computed single value is
	/// projected into, resolved once per type. Only the member is used, so the row type is never instantiated and no
	/// lambda over it is built, which Native AOT could not do for a value type it has no code for.
	/// </summary>
	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The row type is only read for its member, never instantiated.")]
	public static PropertyInfo ResultMemberOf(Type valueType)
	{
		if (ResultMembers.TryGetValue(valueType, out var known))
			return known;

		var row = typeof(ScalarRow<>).MakeGenericType(valueType);
		var result = row.GetProperty(nameof(ScalarRow<>.Result))
			?? throw new InvalidOperationException("The scalar row has no Result property.");
		return ResultMembers.GetOrAdd(valueType, result);
	}

	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Generic method instantiation uses types from the existing expression tree.")]
	[UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Generic method instantiation uses types from the existing expression tree.")]
	protected override Expression VisitMethodCall(MethodCallExpression node)
	{
		var visited = (MethodCallExpression)base.VisitMethodCall(node);

		if (visited.Arguments.Count == 0)
			return visited;

		// An extension method that leaves the rows as they are carries what they hold to the operators after it.
		if (visited.Method.DeclaringType != typeof(Queryable))
		{
			if (_singleValues.TryGetValue(visited.Arguments[0], out var held) && KeepsTheRow(visited.Method))
				_singleValues[visited] = held;
			else if (_groupedAggregations.Contains(visited.Arguments[0]) && KeepsTheRow(visited.Method))
				_ = _groupedAggregations.Add(visited);

			return visited;
		}

		if (_singleValues.TryGetValue(visited.Arguments[0], out var value))
			return TranslatedOperators.Contains(visited.Method.Name) ? Rewrite(visited, value) : visited;

		// A single aggregation after a GroupBy is translated into STATS beside the key, under the name of its method,
		// so a translated operator that reads its value has no column to name. The others leave it as it is.
		if (_groupedAggregations.Contains(visited.Arguments[0]))
		{
			if (TranslatedOperators.Contains(visited.Method.Name) && ReadsTheValue(visited))
			{
				throw new NotSupportedException(
					$"A single aggregation after GroupBy cannot be followed by {visited.Method.Name}, which reads its value: "
					+ "project it into a member, as in 'g => new { Count = g.Count() }', and read the member.");
			}

			if (RowPreservingOperators.Contains(visited.Method.Name))
				_ = _groupedAggregations.Add(visited);

			return visited;
		}

		if (visited.Method.Name == nameof(Queryable.Select) && ExtractLambda(visited) is { } selector && IsScalarSelector(selector))
		{
			// Only a call on the group, such as g.Count() or g.Sum(x => x.Duration): any other selector after a GroupBy,
			// g.Key.ToUpper() among them, is refused there with a message of its own.
			if (visited.Arguments[0] is MethodCallExpression { Method.Name: nameof(Queryable.GroupBy) })
			{
				if (selector.Body.UnwrapConvertExpressions() is MethodCallExpression { Object: null, Arguments: [var group, ..] }
					&& group == selector.Parameters[0])
					_ = _groupedAggregations.Add(visited);
			}
			else
				_singleValues[visited] = new SingleValue(selector, ColumnOf(selector));
		}

		return visited;
	}

	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Generic method instantiation uses types from the existing expression tree.")]
	[UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Generic method instantiation uses types from the existing expression tree.")]
	private Expression Rewrite(MethodCallExpression node, SingleValue value)
	{
		var name = node.Method.Name;

		// A Select right after the scalar one reads the value through the selector that produced it, so the two fold into one.
		if (name == nameof(Queryable.Select) && ExtractLambda(node) is { } outer
			&& node.Arguments[0] is MethodCallExpression inner && ExtractLambda(inner) == value.Selector)
		{
			var composed = Expression.Lambda(Substitute(outer, value.Selector.Body), value.Selector.Parameters);
			var source = inner.Arguments[0];
			var method = node.Method.GetGenericMethodDefinition()
				.MakeGenericMethod(value.Selector.Parameters[0].Type, outer.ReturnType);
			var folded = Expression.Call(method, source, Expression.Quote(composed));
			if (IsScalarSelector(composed))
				_singleValues[folded] = new SingleValue(composed, ColumnOf(composed));
			return folded;
		}

		// A lambda over the row takes it as its only parameter, or, in the predicate of Where, before the element index.
		// A lambda with another second parameter, such as the result selector of GroupBy, is over something else.
		var arguments = node.Arguments
			.Select(a => a is UnaryExpression { Operand: LambdaExpression lambda } && (lambda.Parameters.Count == 1 || name == nameof(Queryable.Where))
				? Expression.Quote(Expression.Lambda(lambda.Type, Substitute(lambda, value.Column), lambda.Parameters))
				: a)
			.ToList();
		var rewritten = node.Update(node.Object, arguments);

		if (RowPreservingOperators.Contains(name))
			_singleValues[rewritten] = value;

		// A Select further down that again returns a single value leaves a column of its own.
		else if (name == nameof(Queryable.Select) && ExtractLambda(rewritten) is { } selector && IsScalarSelector(selector))
			_singleValues[rewritten] = new SingleValue(selector, ColumnOf(selector));

		return rewritten;
	}

	// An operator with a lambda that reads the row, or an aggregate that, without a selector, aggregates the row itself.
	// A Select that returns the row as it is, at most converted, names no column: the translation adds nothing for it.
	private static bool ReadsTheValue(MethodCallExpression node) =>
		node.Arguments.Skip(1).Any(a => a is UnaryExpression { Operand: LambdaExpression lambda }
			&& ExpressionTranslationHelpers.ReadsParameter(lambda.Body, lambda.Parameters[0])
			&& !(node.Method.Name == nameof(Queryable.Select) && lambda.Body.UnwrapConvertExpressions() == lambda.Parameters[0]))
		|| (SelectorAggregates.Contains(node.Method.Name) && node.Arguments.Count == 1);

	// Rows whose element is a single value rather than an object.
	private static bool HoldsASingleValue(Expression source) =>
		TypeHelper.FindGenericType(typeof(IQueryable<>), source.Type) is { } queryable
		&& TypeHelper.IsSingleValueType(queryable.GetGenericArguments()[0]);

	// An extension method that leaves the rows as they are: one that carries query options, Keep, Drop, and RawEsql
	// that keeps the element type. Their own selectors are translated as they were.
	private static bool KeepsTheRow(MethodInfo method) =>
		method.IsDefined(typeof(EsqlQueryOptionsMethodAttribute), inherit: false)
		|| (method.DeclaringType == typeof(EsqlQueryableExtensions) && method.Name switch
		{
			nameof(EsqlQueryableExtensions.Keep) or nameof(EsqlQueryableExtensions.Drop) => true,
			nameof(EsqlQueryableExtensions.RawEsql) => method.GetGenericArguments().Length == 1,
			_ => false
		});

	private static Expression ColumnOf(LambdaExpression selector)
	{
		if (IsFieldPath(selector.Body))
			return selector.Body;

		// The column is the Result member the value is projected into, read off a row of that type.
		var result = ResultMemberOf(selector.ReturnType);
		var row = result.DeclaringType ?? throw new InvalidOperationException("The Result member has no declaring type.");
		return Expression.MakeMemberAccess(Expression.Parameter(row, "row"), result);
	}

	private static Expression Substitute(LambdaExpression lambda, Expression replacement) =>
		ExpressionTranslationHelpers.ReplaceParameter(lambda.Body, lambda.Parameters[0], replacement)
			?? throw new NotSupportedException("The lambda body could not be rewritten.");

	private static LambdaExpression? ExtractLambda(MethodCallExpression node) =>
		node.Arguments.Count >= 2 && node.Arguments[1] is UnaryExpression { Operand: LambdaExpression lambda } ? lambda : null;

	// A path of document members down from a row. The row is the selector's own parameter, or the one an earlier
	// Select read the field from, once an operator after it has been rewritten. A member of a value, such as
	// DateTime.Hour or string.Length, ends the path: the value is computed from the field rather than read as one.
	private static bool IsFieldPath(Expression body)
	{
		var current = body.UnwrapConvertExpressions();
		if (current is not MemberExpression)
			return false;

		while (current is MemberExpression member)
		{
			var parent = member.Expression?.UnwrapConvertExpressions();
			if (parent is null || TypeHelper.IsSingleValueType(parent.Type))
				return false;
			current = parent;
		}

		return current is ParameterExpression;
	}
}

/// <summary>
/// The row a computed scalar Select projects: its single value in the <c>result</c> column. Only its member is read,
/// to name that column; the type is never instantiated.
/// </summary>
/// <remarks>
/// Marked as compiler-generated so that its member is named like the member of an anonymous type,
/// through the naming policy, since no serializer context of the caller knows this type.
/// </remarks>
[CompilerGenerated]
internal sealed class ScalarRow<T>
{
	public T Result { get; set; } = default!;
}
