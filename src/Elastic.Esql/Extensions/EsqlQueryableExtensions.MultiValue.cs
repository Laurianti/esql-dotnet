// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Linq.Expressions;

using Elastic.Esql.Validation;

namespace Elastic.Esql.Extensions;

public static partial class EsqlQueryableExtensions
{
	/// <summary>
	/// States how many values a multi-value field may hold, which lets predicates such
	/// as <c>tags.Any(t => t.StartsWith("a"))</c> be translated: ES|QL reads such a
	/// field one position at a time, so the number of positions has to be fixed when the
	/// query is written. A document holding more values than <paramref name="positions"/>
	/// is left out of the result rather than answered from a prefix of its values.
	/// Without this the shape is not translated.
	/// </summary>
	public static IQueryable<TSource> MultiValueLimit<TSource>(this IQueryable<TSource> source, int positions)
	{
		Verify.NotNull(source);

		if (positions < 1)
			throw new ArgumentOutOfRangeException(nameof(positions), positions, "At least one position has to be read.");

		return CreateQuery(source,
			new Func<IQueryable<TSource>, int, IQueryable<TSource>>(MultiValueLimit).Method,
			Expression.Constant(positions)
		);
	}
}
