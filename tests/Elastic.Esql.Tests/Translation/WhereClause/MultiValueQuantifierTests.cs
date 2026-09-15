// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

namespace Elastic.Esql.Tests.Translation.WhereClause;

/// <summary>
/// Any and All over a multi-value field with a predicate MATCH cannot answer, such as
/// "starts with" or "greater than", and the way negation moves between the two.
/// </summary>
public class MultiValueQuantifierTests : EsqlTestBase
{
	// the unit separator joins the values when a regular expression looks at them
	private const string Sep = "";

	private static string Joined(string field) =>
		$"COALESCE(CONCAT(\"{Sep}\", MV_CONCAT({field}, \"{Sep}\"), \"{Sep}\"), \"{Sep}\")";

	/// <summary>
	/// The whole emitted predicate: the guard that catches a stored value holding the
	/// separator, and then the pattern itself.
	/// </summary>
	private static string Matching(string field, string pattern, string? counted = null)
	{
		var joined = Joined(field);
		var separators = $"(LENGTH({joined}) - LENGTH(REPLACE({joined}, \"{Sep}\", \"\")))";

		return $"({separators} == COALESCE(MV_COUNT({counted ?? field}), 0) + 1 "
			+ $"AND {joined} RLIKE \"\"\"{pattern}\"\"\")";
	}

	[Test]
	public void Any_StartsWith_MatchesSomeValue()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith("wat")))
			.ToString();

		_ = esql.Should().Be(
			$$""""
            FROM products
            | WHERE {{Matching("tags", $".*{Sep}wat[^{Sep}]*{Sep}.*")}}
            """".NativeLineEndings());
	}

	[Test]
	public void Any_EndsWith_MatchesSomeValue()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.EndsWith("al")))
			.ToString();

		_ = esql.Should().Be(
			$$""""
            FROM products
            | WHERE {{Matching("tags", $".*{Sep}[^{Sep}]*al{Sep}.*")}}
            """".NativeLineEndings());
	}

	[Test]
	public void Any_Contains_MatchesSomeValue()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.Contains("at")))
			.ToString();

		_ = esql.Should().Be(
			$$""""
            FROM products
            | WHERE {{Matching("tags", $".*{Sep}[^{Sep}]*at[^{Sep}]*{Sep}.*")}}
            """".NativeLineEndings());
	}

	[Test]
	public void All_StartsWith_RequiresEveryValueToMatch()
	{
		// the joined string must be a run of matching values; the empty field is one too
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.All(t => t.StartsWith("i")))
			.ToString();

		_ = esql.Should().Be(
			$$""""
            FROM products
            | WHERE {{Matching("tags", $"({Sep}i[^{Sep}]*)*{Sep}")}}
            """".NativeLineEndings());
	}

	[Test]
	public void Any_WithANegatedPredicate_BecomesNotAll()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => !t.StartsWith("i")))
			.ToString();

		_ = esql.Should().Be(
			$$""""
            FROM products
            | WHERE NOT {{Matching("tags", $"({Sep}i[^{Sep}]*)*{Sep}")}}
            """".NativeLineEndings());
	}

	[Test]
	public void All_WithANegatedPredicate_BecomesNotAny()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.All(t => !t.Contains("at")))
			.ToString();

		_ = esql.Should().Be(
			$$""""
            FROM products
            | WHERE NOT {{Matching("tags", $".*{Sep}[^{Sep}]*at[^{Sep}]*{Sep}.*")}}
            """".NativeLineEndings());
	}

	[Test]
	public void Any_NotEqual_BecomesNotAllEqual()
	{
		// "some value differs from x" is "not every value is x"
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t != "iot"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE NOT (tags IS NULL OR (MV_COUNT(MV_DEDUPE(tags)) == 1 AND MATCH(tags, "iot")))
            """.NativeLineEndings());
	}

	[Test]
	public void NullGuardsOnTheElement_AreDropped()
	{
		// generated predicates guard the element against null; a stored value never is
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t != null && t.StartsWith("wat")))
			.ToString();

		_ = esql.Should().Be(
			$$""""
            FROM products
            | WHERE {{Matching("tags", $".*{Sep}wat[^{Sep}]*{Sep}.*")}}
            """".NativeLineEndings());
	}

	[Test]
	public void NegatedNullGuardedPredicate_IsReadThroughTheGuard()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => !(t != null && t.StartsWith("wat"))))
			.ToString();

		_ = esql.Should().Be(
			$$""""
            FROM products
            | WHERE NOT {{Matching("tags", $"({Sep}wat[^{Sep}]*)*{Sep}")}}
            """".NativeLineEndings());
	}

	[Test]
	public void Any_InAConstantList_TranslatesToMatchesInOr()
	{
		var wanted = new[] { "iot", "water" };

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => wanted.Contains(t)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (MATCH(tags, "iot") OR MATCH(tags, "water"))
            """.NativeLineEndings());
	}

	[Test]
	public void All_InAConstantList_RequiresEveryValueToBeListed()
	{
		var wanted = new[] { "iot", "water" };

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.All(t => wanted.Contains(t)))
			.ToString();

		_ = esql.Should().Be(
			$$""""
            FROM products
            | WHERE {{Matching("tags", $"({Sep}(iot|water))*{Sep}")}}
            """".NativeLineEndings());
	}

	[Test]
	public void All_InAConstantListOfIntegers_ComparesTheirText()
	{
		var wanted = new[] { 5, 42 };

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.All(r => wanted.Contains(r)))
			.ToString();

		_ = esql.Should().Be(
			$$""""
            FROM products
            | WHERE {{Matching("TO_STRING(ratings)", $"({Sep}(5|42))*{Sep}", "ratings")}}
            """".NativeLineEndings());
	}

	[Test]
	public void ReservedRegexCharacters_AreEscaped()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith("a.b(c)|d")))
			.ToString();

		_ = esql.Should().Be(
			$$""""
            FROM products
            | WHERE {{Matching("tags", $@".*{Sep}a\.b\(c\)\|d[^{Sep}]*{Sep}.*")}}
            """".NativeLineEndings());
	}

	[Test]
	public void AValueHoldingTheSeparator_IsRefused()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith("a" + Sep + "b")));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void Any_GreaterThan_ComparesTheLargestValue()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.Any(r => r > 3))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (ratings IS NOT NULL AND MV_MAX(ratings) > 3)
            """.NativeLineEndings());
	}

	[Test]
	public void All_GreaterThanOrEqual_ComparesTheSmallestValue()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.All(r => r >= 3))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (ratings IS NULL OR MV_MIN(ratings) >= 3)
            """.NativeLineEndings());
	}

	[Test]
	public void All_GreaterThan_ComparesTheSmallestValue()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.All(r => r > 3))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (ratings IS NULL OR MV_MIN(ratings) > 3)
            """.NativeLineEndings());
	}

	[Test]
	public void Any_LessThan_ComparesTheSmallestValue()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.Any(r => r < 3))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (ratings IS NOT NULL AND MV_MIN(ratings) < 3)
            """.NativeLineEndings());
	}

	[Test]
	public void AComparisonWithTheElementOnTheRight_IsFlipped()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.Any(r => 3 < r))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (ratings IS NOT NULL AND MV_MAX(ratings) > 3)
            """.NativeLineEndings());
	}

	[Test]
	public void Any_WithAPredicateThatCannotBeTranslated_StillThrows()
	{
		// nothing approximate: an unknown predicate keeps the existing behaviour
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.Length > 3));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void ANegatedQuantifierStaysDefinedOverAMissingField()
	{
		// MV_MAX is null over a missing field, so the predicate has to say explicitly
		// that the field is present, or an enclosing NOT would answer neither way
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.All(r => !(r > 3)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE NOT (ratings IS NOT NULL AND MV_MAX(ratings) > 3)
            """.NativeLineEndings());
	}

	[Test]
	public void ANegatedAnyOverAMissingFieldIsTrue()
	{
		// LINQ reads a missing field as an empty sequence: !Any() holds there
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => !p.Tags.Any())
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE NOT COALESCE(MV_COUNT(tags), 0) > 0
            """.NativeLineEndings());
	}
}
