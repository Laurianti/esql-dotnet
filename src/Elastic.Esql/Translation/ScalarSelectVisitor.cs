// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

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
		nameof(Queryable.Take),
		nameof(Queryable.Skip),
		nameof(Queryable.Distinct)
	];

	private static readonly HashSet<string> SelectorAggregates =
	[
		nameof(Queryable.Sum),
		nameof(Queryable.Average),
		nameof(Queryable.Min),
		nameof(Queryable.Max)
	];

	// The expression that stands for the single value of each row after a scalar Select, by the call that produces those rows.
	private readonly Dictionary<Expression, ScalarRow> _scalarRows = [];

	// The single aggregations that follow a GroupBy.
	private readonly HashSet<Expression> _groupedAggregations = [];

	private readonly record struct ScalarRow(LambdaExpression Selector, Expression Column);

	/// <summary>A selector returning a single value: a field, or a value computed from the row.</summary>
	public static bool IsScalarSelector(LambdaExpression lambda) =>
		lambda.Parameters.Count is 1 or 2
		&& IsScalarType(lambda.ReturnType)
		&& lambda.Body is not NewExpression and not MemberInitExpression
		&& lambda.Body.UnwrapConvertExpressions() != lambda.Parameters[0];

	/// <summary>A scalar selector that computes its value rather than reading a field.</summary>
	public static bool IsComputedScalarSelector(LambdaExpression lambda) =>
		IsScalarSelector(lambda) && !IsFieldPath(lambda.Body, lambda.Parameters[0]);

	/// <summary>
	/// Wraps a computed scalar selector into <c>new ScalarResult&lt;T&gt; { Result = ... }</c>,
	/// which projects into the <c>result</c> column.
	/// </summary>
	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The wrapper is closed over a type taken from the existing expression tree.")]
	public static LambdaExpression WrapComputedSelector(LambdaExpression lambda)
	{
		var rowType = typeof(ScalarResult<>).MakeGenericType(lambda.ReturnType);
		var body = Expression.MemberInit(
			Expression.New(rowType),
			Expression.Bind(rowType.GetProperty(nameof(ScalarResult<>.Result)) ?? throw new InvalidOperationException("The scalar row has no Result property."), lambda.Body));
		return Expression.Lambda(body, lambda.Parameters);
	}

	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Generic method instantiation uses types from the existing expression tree.")]
	[UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Generic method instantiation uses types from the existing expression tree.")]
	protected override Expression VisitMethodCall(MethodCallExpression node)
	{
		var visited = (MethodCallExpression)base.VisitMethodCall(node);

		if (visited.Method.DeclaringType != typeof(Queryable) || visited.Arguments.Count == 0)
			return visited;

		if (_scalarRows.TryGetValue(visited.Arguments[0], out var row))
			return Rewrite(visited, row);

		// A single aggregation after a GroupBy is translated into STATS beside the key, so the row is not a single value.
		if (_groupedAggregations.Contains(visited.Arguments[0]))
		{
			throw new NotSupportedException(
				$"A single aggregation after GroupBy cannot be followed by {visited.Method.Name}: project it into a member, "
				+ "as in 'g => new { Count = g.Count() }', and read the member.");
		}

		if (visited.Method.Name == nameof(Queryable.Select) && ExtractLambda(visited) is { } selector && IsScalarSelector(selector))
		{
			if (visited.Arguments[0] is MethodCallExpression { Method.Name: nameof(Queryable.GroupBy) })
				_ = _groupedAggregations.Add(visited);
			else
				_scalarRows[visited] = new ScalarRow(selector, ColumnOf(selector));
		}

		return visited;
	}

	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Generic method instantiation uses types from the existing expression tree.")]
	[UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Generic method instantiation uses types from the existing expression tree.")]
	private Expression Rewrite(MethodCallExpression node, ScalarRow row)
	{
		var name = node.Method.Name;

		// A Select right after the scalar one reads the value through the selector that produced it, so the two fold into one.
		if (name == nameof(Queryable.Select) && ExtractLambda(node) is { } outer
			&& node.Arguments[0] is MethodCallExpression inner && ExtractLambda(inner) == row.Selector)
		{
			var composed = Expression.Lambda(Substitute(outer, row.Selector.Body), row.Selector.Parameters);
			var source = inner.Arguments[0];
			var method = node.Method.GetGenericMethodDefinition()
				.MakeGenericMethod(row.Selector.Parameters[0].Type, outer.ReturnType);
			var folded = Expression.Call(method, source, Expression.Quote(composed));
			if (IsScalarSelector(composed))
				_scalarRows[folded] = new ScalarRow(composed, ColumnOf(composed));
			return folded;
		}

		// Sum(), Max() and the like name no field: they aggregate the single value.
		if (SelectorAggregates.Contains(name) && node.Arguments.Count == 1)
		{
			var elementType = row.Selector.ReturnType;
			var parameter = Expression.Parameter(elementType, "x");
			var selector = Expression.Lambda(row.Column, parameter);
			return Expression.Call(FindSelectorOverload(node.Method, elementType), node.Arguments[0], Expression.Quote(selector));
		}

		var arguments = node.Arguments
			.Select(a => a is UnaryExpression { Operand: LambdaExpression lambda } && lambda.Parameters.Count == 1
				? Expression.Quote(Expression.Lambda(lambda.Type, Substitute(lambda, row.Column), lambda.Parameters))
				: a)
			.ToList();
		var rewritten = node.Update(node.Object, arguments);

		if (RowPreservingOperators.Contains(name))
			_scalarRows[rewritten] = row;

		// A Select further down that again returns a single value leaves a column of its own.
		else if (name == nameof(Queryable.Select) && ExtractLambda(rewritten) is { } selector && IsScalarSelector(selector))
			_scalarRows[rewritten] = new ScalarRow(selector, ColumnOf(selector));

		return rewritten;
	}

	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The wrapper is closed over a type taken from the existing expression tree.")]
	private static Expression ColumnOf(LambdaExpression selector)
	{
		if (IsFieldPath(selector.Body, selector.Parameters[0]))
			return selector.Body;

		// The column is the member the wrapped selector binds, read off a row of the wrapper type.
		var row = (MemberInitExpression)WrapComputedSelector(selector).Body;
		var binding = (MemberAssignment)row.Bindings[0];
		return Expression.MakeMemberAccess(Expression.Parameter(row.Type, "row"), binding.Member);
	}

	// Sum and Average have one overload per numeric type; Min and Max take the result type as a generic argument.
	private static readonly Dictionary<(string, Type), MethodInfo> NumericSelectorAggregates = new()
	{
		[(nameof(Queryable.Sum), typeof(int))] = new Func<IQueryable<int>, Expression<Func<int, int>>, int>(Queryable.Sum).Method,
		[(nameof(Queryable.Sum), typeof(long))] = new Func<IQueryable<long>, Expression<Func<long, long>>, long>(Queryable.Sum).Method,
		[(nameof(Queryable.Sum), typeof(float))] = new Func<IQueryable<float>, Expression<Func<float, float>>, float>(Queryable.Sum).Method,
		[(nameof(Queryable.Sum), typeof(double))] = new Func<IQueryable<double>, Expression<Func<double, double>>, double>(Queryable.Sum).Method,
		[(nameof(Queryable.Sum), typeof(decimal))] = new Func<IQueryable<decimal>, Expression<Func<decimal, decimal>>, decimal>(Queryable.Sum).Method,
		[(nameof(Queryable.Average), typeof(int))] = new Func<IQueryable<int>, Expression<Func<int, int>>, double>(Queryable.Average).Method,
		[(nameof(Queryable.Average), typeof(long))] = new Func<IQueryable<long>, Expression<Func<long, long>>, double>(Queryable.Average).Method,
		[(nameof(Queryable.Average), typeof(float))] = new Func<IQueryable<float>, Expression<Func<float, float>>, float>(Queryable.Average).Method,
		[(nameof(Queryable.Average), typeof(double))] = new Func<IQueryable<double>, Expression<Func<double, double>>, double>(Queryable.Average).Method,
		[(nameof(Queryable.Average), typeof(decimal))] = new Func<IQueryable<decimal>, Expression<Func<decimal, decimal>>, decimal>(Queryable.Average).Method,
		[(nameof(Queryable.Sum), typeof(int?))] = new Func<IQueryable<int?>, Expression<Func<int?, int?>>, int?>(Queryable.Sum).Method,
		[(nameof(Queryable.Sum), typeof(long?))] = new Func<IQueryable<long?>, Expression<Func<long?, long?>>, long?>(Queryable.Sum).Method,
		[(nameof(Queryable.Sum), typeof(float?))] = new Func<IQueryable<float?>, Expression<Func<float?, float?>>, float?>(Queryable.Sum).Method,
		[(nameof(Queryable.Sum), typeof(double?))] = new Func<IQueryable<double?>, Expression<Func<double?, double?>>, double?>(Queryable.Sum).Method,
		[(nameof(Queryable.Sum), typeof(decimal?))] = new Func<IQueryable<decimal?>, Expression<Func<decimal?, decimal?>>, decimal?>(Queryable.Sum).Method,
		[(nameof(Queryable.Average), typeof(int?))] = new Func<IQueryable<int?>, Expression<Func<int?, int?>>, double?>(Queryable.Average).Method,
		[(nameof(Queryable.Average), typeof(long?))] = new Func<IQueryable<long?>, Expression<Func<long?, long?>>, double?>(Queryable.Average).Method,
		[(nameof(Queryable.Average), typeof(float?))] = new Func<IQueryable<float?>, Expression<Func<float?, float?>>, float?>(Queryable.Average).Method,
		[(nameof(Queryable.Average), typeof(double?))] = new Func<IQueryable<double?>, Expression<Func<double?, double?>>, double?>(Queryable.Average).Method,
		[(nameof(Queryable.Average), typeof(decimal?))] = new Func<IQueryable<decimal?>, Expression<Func<decimal?, decimal?>>, decimal?>(Queryable.Average).Method
	};

	private static readonly MethodInfo MinWithSelector =
		new Func<IQueryable<object>, Expression<Func<object, object>>, object?>(Queryable.Min).Method.GetGenericMethodDefinition();

	private static readonly MethodInfo MaxWithSelector =
		new Func<IQueryable<object>, Expression<Func<object, object>>, object?>(Queryable.Max).Method.GetGenericMethodDefinition();

	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Generic method instantiation uses types from the existing expression tree.")]
	[UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Generic method instantiation uses types from the existing expression tree.")]
	private static MethodInfo FindSelectorOverload(MethodInfo parameterless, Type elementType) =>
		parameterless.Name switch
		{
			nameof(Queryable.Min) => MinWithSelector.MakeGenericMethod(elementType, elementType),
			nameof(Queryable.Max) => MaxWithSelector.MakeGenericMethod(elementType, elementType),
			_ when NumericSelectorAggregates.TryGetValue((parameterless.Name, elementType), out var method) => method,
			_ => throw new NotSupportedException($"{parameterless.Name} over a {elementType.Name} is not supported.")
		};

	private static Expression Substitute(LambdaExpression lambda, Expression replacement) =>
		new ParameterReplacer(lambda.Parameters[0], replacement).Visit(lambda.Body);

	private static LambdaExpression? ExtractLambda(MethodCallExpression node) =>
		node.Arguments.Count >= 2 && node.Arguments[1] is UnaryExpression { Operand: LambdaExpression lambda } ? lambda : null;

	// A path of document members down from the row. A member of a value, such as DateTime.Hour or string.Length,
	// ends the path: the value is computed from the field rather than read as one.
	private static bool IsFieldPath(Expression body, ParameterExpression parameter)
	{
		var current = body.UnwrapConvertExpressions();
		if (current is not MemberExpression)
			return false;

		while (current is MemberExpression member)
		{
			var parent = member.Expression?.UnwrapConvertExpressions();
			if (parent is null || IsScalarType(parent.Type))
				return false;
			current = parent;
		}

		return current == parameter;
	}

	private static bool IsScalarType(Type type)
	{
		var t = Nullable.GetUnderlyingType(type) ?? type;
		return t.IsPrimitive || t == typeof(decimal) || t == typeof(string) || t.IsEnum
			|| t == typeof(DateTime) || t == typeof(DateTimeOffset);
	}

	private sealed class ParameterReplacer(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
	{
		protected override Expression VisitParameter(ParameterExpression node) =>
			node == parameter ? replacement : base.VisitParameter(node);
	}
}

/// <summary>The row a computed scalar Select projects: its single value in the <c>result</c> column.</summary>
/// <remarks>
/// Marked as compiler-generated so that its member is named like the member of an anonymous type,
/// through the naming policy, since no serializer context of the caller knows this type.
/// </remarks>
[CompilerGenerated]
internal sealed class ScalarResult<T>
{
	public T Result { get; set; } = default!;
}
