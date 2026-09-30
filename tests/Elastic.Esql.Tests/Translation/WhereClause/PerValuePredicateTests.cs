// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

namespace Elastic.Esql.Tests.Translation.WhereClause;

/// <summary>
/// Any, All and Contains over the first values of a multi-value field, as many as Take
/// states on the field. Each position is read with MV_SLICE and tested on its own, so a
/// predicate MATCH cannot answer, such as "starts with", is answered too, and every
/// predicate reads the positions Take states rather than the whole field.
/// </summary>
public class PerValuePredicateTests : EsqlTestBase
{
	// how many positions most of these queries state with Take, one MV_SLICE each
	private const int Positions = 4;

	private static string Slice(string field, int position) => $"MV_SLICE({field}, {position}, {position})";

	/// <summary>The positions are one term of whatever encloses them.</summary>
	private static string Chain(IEnumerable<string> terms, string separator, int positions) =>
		positions > 1 ? $"({string.Join(separator, terms)})" : string.Join(separator, terms);

	/// <summary>Any: the test holds at some position; an absent value does not count.</summary>
	private static string AnyOf(string field, Func<string, string> test, int positions = Positions) =>
		Chain(Enumerable.Range(0, positions).Select(position => $"COALESCE({test(Slice(field, position))}, false)"), " OR ", positions);

	/// <summary>All: the test holds at every position that has a value.</summary>
	private static string AllOf(string field, Func<string, string> test, int positions = Positions) =>
		Chain(Enumerable.Range(0, positions).Select(position =>
		{
			var value = Slice(field, position);
			return $"COALESCE({value} IS NULL OR {test(value)}, true)";
		}), " AND ", positions);

	[Test]
	public void Where_AnyStartsWith_TestsEachPosition()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"STARTS_WITH({value}, \"wat\")")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyEndsWith_TestsEachPosition()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.EndsWith("al", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"ENDS_WITH({value}, \"al\")")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyContains_TestsEachPositionWithLike()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.Contains("at", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"{value} LIKE \"*at*\"")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AllStartsWith_TestsEveryPositionThatHasAValue()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).All(t => t.StartsWith("io", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AllOf("tags", value => $"STARTS_WITH({value}, \"io\")")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_StartsWithWithoutAComparison_IsTranslatedAsOrdinal()
	{
		// the one-argument overload, which a single field translates as well
#pragma warning disable CA1310 // the one-argument overload is the call shape under test
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.StartsWith("wat")))
			.ToString();
#pragma warning restore CA1310

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"STARTS_WITH({value}, \"wat\")")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_StartsWithIgnoringCase_ThrowsNotSupported()
	{
		// ES|QL string matching is ordinal and case-sensitive, as for a single field
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.StartsWith("wat", StringComparison.OrdinalIgnoreCase)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*other than StringComparison.Ordinal*");
	}

	[Test]
	public void Where_AnyWithANegatedPredicate_BecomesNotAll()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => !t.StartsWith("io", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE NOT {{AllOf("tags", value => $"STARTS_WITH({value}, \"io\")")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AllWithANegatedPredicate_BecomesNotAny()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).All(t => !t.Contains("at", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE NOT {{AnyOf("tags", value => $"{value} LIKE \"*at*\"")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_NullGuardsOnTheElement_AreDropped()
	{
		// a stored value is never null, so the guard says nothing
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t != null && t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"STARTS_WITH({value}, \"wat\")")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_NegatedNullGuardedPredicate_IsReadThroughTheGuard()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => !(t != null && t.StartsWith("wat", StringComparison.Ordinal))))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE NOT {{AllOf("tags", value => $"STARTS_WITH({value}, \"wat\")")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TwoPredicatesUnderOneNegation_StayDefinite()
	{
		// each position answers true or false, never null, so the NOT over both is exact
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => !(p.Tags.Take(Positions).Any(t => t.StartsWith("ab", StringComparison.Ordinal))
				&& p.Categories.Take(Positions).Any(c => c.StartsWith("cd", StringComparison.Ordinal))))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE NOT ({{AnyOf("tags", value => $"STARTS_WITH({value}, \"ab\")")}} AND {{AnyOf("categories", value => $"STARTS_WITH({value}, \"cd\")")}})
            """.NativeLineEndings());
	}

	[Test]
	public void Where_PositionsNextToAScalarPredicate_KeepTheirParentheses()
	{
		// without them the OR between the positions would bind looser than the AND
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.StartsWith("wat", StringComparison.Ordinal)) && p.Name == "pump")
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE ({{AnyOf("tags", value => $"STARTS_WITH({value}, \"wat\")")}} AND name == "pump")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_NegatedAnyWithoutAPredicate_HoldsForAMissingField()
	{
		// Take(n) of a field that holds a value still holds one
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => !p.Tags.Take(Positions).Any())
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE NOT tags IS NOT NULL
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeWithEquality_TestsOnlyTheFirstPositions()
	{
		// MATCH would find the value anywhere in the field, where Take(3) reads three
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(3).Any(t => t == "water"))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"{value} == \"water\"", positions: 3)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeWithContains_TestsOnlyTheFirstPositions()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(3).Contains("water"))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"{value} == \"water\"", positions: 3)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeWithAnOrdering_TestsOnlyTheFirstPositions()
	{
		// MV_MAX would read every value, where Take(2) reads two
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.Take(2).Any(r => r > 3))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("ratings", value => $"{value} > 3", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AllTakeWithEquality_TestsEveryFirstPosition()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.Take(2).All(r => r == 4))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AllOf("ratings", value => $"{value} == 4", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeWithTheElementOnTheRight_IsFlipped()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.Take(2).Any(r => 3 < r))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("ratings", value => $"{value} > 3", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeWithANotEqual_BecomesNotAllEqual()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t != "iot"))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE NOT {{AllOf("tags", value => $"{value} == \"iot\"")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeOverAConstantList_TestsEachValueAtEachPosition()
	{
		var wanted = new[] { "iot", "water" };

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => wanted.Contains(t)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"({value} == \"iot\" OR {value} == \"water\")")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AllTakeOverAConstantList_RequiresEveryValueToBeListed()
	{
		var wanted = new[] { "iot", "water" };

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).All(t => wanted.Contains(t)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AllOf("tags", value => $"({value} == \"iot\" OR {value} == \"water\")")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeOverAConstantListOfIntegers_ComparesTheValues()
	{
		var wanted = new[] { 5, 42 };

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.Take(Positions).All(r => wanted.Contains(r)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AllOf("ratings", value => $"({value} == 5 OR {value} == 42)")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyTakeOverAnEmptyConstantList_IsFalse()
	{
		var wanted = Array.Empty<string>();

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => wanted.Contains(t)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE false
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AllTakeOverAnEmptyConstantList_HoldsForAnEmptyField()
	{
		var wanted = Array.Empty<string>();

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).All(t => wanted.Contains(t)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE tags IS NULL
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeOverUnsignedValues_ComparesThem()
	{
		// every type ES|QL compares is read the same way, unsigned ones included
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Counts.Take(2).Any(c => c == 7u))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("counts", value => $"{value} == 7", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeOverFloatingPointValues_ComparesThem()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Weights.Take(2).Any(w => w > 1.5))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("weights", value => $"{value} > 1.5", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_ACapturedValue_IsOneParameterForAllPositions()
	{
		var prefix = "wat";

		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.StartsWith(prefix, StringComparison.Ordinal)));

		var esql = query.ToEsqlString(inlineParameters: false);

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"STARTS_WITH({value}, ?prefix)")}}
            """.NativeLineEndings());
		_ = query.GetParameters()!.Parameters.Should().ContainSingle();
	}

	[Test]
	public void Where_ACapturedEqualityValue_IsOneParameterForAllPositions()
	{
		var tag = "water";

		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(3).Any(t => t == tag));

		var esql = query.ToEsqlString(inlineParameters: false);

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"{value} == ?tag", positions: 3)}}
            """.NativeLineEndings());
		_ = query.GetParameters()!.Parameters["tag"].GetString().Should().Be("water");
	}

	[Test]
	public void Where_ACapturedContainsPattern_StaysInline()
	{
		// a LIKE pattern is written inline, as for a single field
		var fragment = "ate";

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.Contains(fragment, StringComparison.Ordinal)))
			.ToEsqlString(inlineParameters: false);

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"{value} LIKE \"*ate*\"")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AQuoteInTheValue_IsEscaped()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.StartsWith("a\"b", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"STARTS_WITH({value}, \"a\\\"b\")")}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AControlCharacterInTheValue_IsEscaped()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.StartsWith("a\nb", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Contain(@"STARTS_WITH(MV_SLICE(tags, 0, 0), ""a\nb"")");
	}

	[Test]
	public void Where_AQuoteInAContainsValue_IsEscapedOnce()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.Contains("a\"b", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Contain(@"LIKE ""*a\""b*""");
	}

	[Test]
	public void Where_AWildcardInAContainsValue_IsEscaped()
	{
		// the value is matched as written, not as a pattern of its own
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.Contains("a*b", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Contain(@"LIKE ""*a\\*b*""");
	}

	[Test]
	public void Where_TakeSetsThePositionsRead()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"STARTS_WITH({value}, \"wat\")", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeOfOne_ReadsOnePosition()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(1).Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE COALESCE(STARTS_WITH(MV_SLICE(tags, 0, 0), "wat"), false)
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AFieldWithMoreValues_IsAnsweredOnItsFirstPositions()
	{
		// Take(n) reads the first n values whatever the field holds past them, so no
		// document is left out for holding more
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().NotContain("MV_COUNT").And.NotContain("CASE(");
	}

	[Test]
	public void Where_TheLargestTake_IsAccepted()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(256).Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Contain("MV_SLICE(tags, 255, 255)").And.NotContain("MV_SLICE(tags, 256, 256)");
	}

	[Test]
	public void Where_ATakeAboveWhatElasticsearchParses_ThrowsNotSupported()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(257).Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*at most 256*");
	}

	[Test]
	public void Where_ATakeBelowOne_ThrowsNotSupported()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(0).Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*at least one*");
	}

	[Test]
	public void Where_ATakeOfAField_ThrowsNotSupported()
	{
		// the number of positions has to be known when the query is written
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(p.Ratings.Count).Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*constant count*");
	}

	[Test]
	public void Where_PositionsAndValuesTogether_AreBounded()
	{
		var wanted = Enumerable.Range(0, 100).Select(i => $"tag{i}").ToArray();

		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(200).Any(t => wanted.Contains(t)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*together*");
	}

	[Test]
	public void Where_APerValuePredicateWithoutTake_ThrowsNotSupported()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.All(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*Take(n)*");
	}

	[Test]
	public void Where_APredicateThatCannotBeTranslated_StillThrows()
	{
		// nothing approximate: an unknown predicate keeps the existing behaviour
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.Length > 3));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*individual values of tags*");
	}

	[Test]
	public void Where_TakeOverAPropertyWithAJsonConverter_ThrowsNotSupported()
	{
		// the field holds the values the converter writes, which the positions would not find
		var query = CreateQuery<ConvertedTagsProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*JsonConverter*");
	}

	[Test]
	public void Where_TakeAfterALimit_IsNotRefused()
	{
		// the positions are read without MATCH, which alone Elasticsearch refuses after LIMIT
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Take(10)
			.Where(p => p.Tags.Take(1).Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | LIMIT 10
            | WHERE COALESCE(STARTS_WITH(MV_SLICE(tags, 0, 0), "wat"), false)
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeWithContainsOfAField_ThrowsNotSupported()
	{
		// LIKE takes its pattern as a literal
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Positions).Any(t => t.Contains(p.Name)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*Contains over the values of tags*literal*");
	}

	[Test]
	public void Where_TakeStartsWithAChar_ReadsItAsAOneCharacterString()
	{
		// t.StartsWith('w') is t.StartsWith("w")
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).Any(t => t.StartsWith('w')))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"STARTS_WITH({value}, \"w\")", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeEndsWithAChar_ReadsItAsAOneCharacterString()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).Any(t => t.EndsWith('l')))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"ENDS_WITH({value}, \"l\")", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeContainingAChar_ReadsItAsAOneCharacterString()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).Any(t => t.Contains('a')))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"{value} LIKE \"*a*\"", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeContainingACharOrdinally_ReadsItAsAOneCharacterString()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).Any(t => t.Contains('a', StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"{value} LIKE \"*a*\"", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	[Arguments(StringComparison.OrdinalIgnoreCase)]
	[Arguments(StringComparison.CurrentCulture)]
	[Arguments(StringComparison.InvariantCultureIgnoreCase)]
	public void Where_TakeContainingACharNotOrdinally_ThrowsNotSupported(StringComparison comparison)
	{
		// as for a string: ES|QL string matching is ordinal and case-sensitive
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).Any(t => t.Contains('a', comparison)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*StringComparison*");
	}

	[Test]
	public void Where_TakeAllStartsWithAChar_ReadsEveryPosition()
	{
		// the refusal of All is for QSTR alone; with Take(n) every position is read
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).All(t => t.StartsWith('w')))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AllOf("tags", value => $"STARTS_WITH({value}, \"w\")", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeStartsWithACapturedChar_IsOneStringParameter()
	{
		// the parameter carries the one-character string, not a char the JSON would write its own way
		var letter = 'w';

		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).Any(t => t.StartsWith(letter)));

		var esql = query.ToEsqlString(inlineParameters: false);

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"STARTS_WITH({value}, ?letter)", positions: 2)}}
            """.NativeLineEndings());
		_ = query.GetParameters()!.Parameters["letter"].GetString().Should().Be("w");
	}

	[Test]
	[Arguments('*', @"LIKE ""*\\**""")]
	[Arguments('?', @"LIKE ""*\\?*""")]
	[Arguments('\\', @"LIKE ""*\\\\*""")]
	public void Where_TakeContainingAWildcardChar_EscapesIt(char wildcard, string expected)
	{
		// the character is matched as written, not as a pattern of its own
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).Any(t => t.Contains(wildcard)))
			.ToString();

		_ = esql.Should().Contain(expected);
	}

	[Test]
	public void Where_TakeStartsWithAQuoteChar_IsEscaped()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).Any(t => t.StartsWith('"')))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"STARTS_WITH({value}, \"\\\"\")", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeStartsWithABackslashChar_IsEscaped()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).Any(t => t.StartsWith('\\')))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE {{AnyOf("tags", value => $"STARTS_WITH({value}, \"\\\\\")", positions: 2)}}
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TakeOfARangeFromTheStart_ThrowsNotSupported()
	{
		// Take(Range) takes two arguments as Take(int) does, and is told apart by its type; an
		// expression tree takes no .. literal, so the range is built by a method
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Range.EndAt(4)).Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*Take with a range*count*Take(4)*");
	}

	[Test]
	public void Where_TakeOfARangeWithBothEnds_ThrowsNotSupported()
	{
		// the range is refused whatever its ends
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(new Range(0, 4)).Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*Take with a range*count*Take(4)*");
	}

	[Test]
	public void Where_TakeOfARangeFromTheEnd_ThrowsNotSupported()
	{
		// the last values have no position counted from the start
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Range.StartAt(Index.FromEnd(2))).Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*Take with a range*count*Take(4)*");
	}

	[Test]
	public void Where_TakeOfARangeWithAnyWithoutAPredicate_ThrowsNotSupported()
	{
		// the Take is read before the predicate, so an Any without one is refused alike
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Range.EndAt(4)).Any());

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*Take with a range*count*Take(4)*");
	}

	[Test]
	public void Where_TakeOfARangeWithContains_ThrowsNotSupported()
	{
		// as for Any
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(Range.EndAt(4)).Contains("water"));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*Take with a range*count*Take(4)*");
	}

	[Test]
	public void Where_TakeOfACapturedRange_ThrowsNotSupported()
	{
		// a captured range is a range all the same
		var firstFour = ..4;

		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(firstFour).Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*Take with a range*count*Take(4)*");
	}
}
