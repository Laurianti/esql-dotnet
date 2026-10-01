// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Text.Json;

namespace Elastic.Esql.Tests.Translation.WhereClause;

/// <summary>
/// Any over StartsWith, EndsWith or Contains translates to QSTR, whose argument is written in
/// the query string syntax rather than in ES|QL: a field name there is escaped with a
/// backslash, where ES|QL quotes it with backticks, and a bare AND, OR or NOT is an operator
/// there.
/// </summary>
public class QueryStringCornerCaseTests : EsqlTestBase
{
	[Test]
	public void Where_AnyStartsWithOverAHyphenatedField_EscapesTheNameForTheQueryString()
	{
		// an unescaped '-' would read as a prohibit operator
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.HyphenatedTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`user-tags` IS NOT NULL AND QSTR("user\\-tags:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldNamedAfterAKeyword_NamesItBare()
	{
		// the backticks ES|QL needs for a keyword are no part of the name
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.KeywordTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`sort` IS NOT NULL AND QSTR("sort:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldWithASpace_EscapesTheSpace()
	{
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.SpacedTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`os name` IS NOT NULL AND QSTR("os\\ name:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverANonAsciiField_NamesItBare()
	{
		// ES|QL quotes a name that is not ASCII, the query string syntax does not need to
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.NonAsciiTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`größe` IS NOT NULL AND QSTR("größe:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldWithABacktick_KeepsOneBacktick()
	{
		// ES|QL doubles a backtick inside a quoted name, the query string syntax reserves none
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.BacktickTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`back``tick` IS NOT NULL AND QSTR("back`tick:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldWithAColon_EscapesTheColon()
	{
		// an unescaped ':' would end the field name early, at ns
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.ColonTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`ns:tags` IS NOT NULL AND QSTR("ns\\:tags:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldWithAWildcard_EscapesTheWildcard()
	{
		// an unescaped '*' in a field name would query every field it matches
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.WildcardTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`tags*` IS NOT NULL AND QSTR("tags\\*:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAPathWithAQuotedSegment_EscapesOnlyThatSegment()
	{
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.DottedHyphenatedTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`user-agent`.tags IS NOT NULL AND QSTR("user\\-agent.tags:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAHyphenatedMultiField_EscapesTheSubfield()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.MultiField("key-word").Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags.`key-word` IS NOT NULL AND QSTR("tags.key\\-word:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithAnAngleBracket_EscapesIt()
	{
		// escaped, < and > are read as themselves rather than as a range
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith("a<b>c", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:a\\<b\\>c*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldNamedAnd_EscapesALetterOfTheOperator()
	{
		// the query string syntax reads a bare AND as its operator, whatever is escaped around it
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.AndTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`AND` IS NOT NULL AND QSTR("\\AND:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldNamedOr_EscapesALetterOfTheOperator()
	{
		// the query string syntax reads a bare OR as its operator, whatever is escaped around it
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.OrTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`OR` IS NOT NULL AND QSTR("\\OR:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldNamedNot_EscapesALetterOfTheOperator()
	{
		// the query string syntax reads a bare NOT as its operator, whatever is escaped around it
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.NotTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`NOT` IS NOT NULL AND QSTR("\\NOT:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldWithABackslash_EscapesItTwice()
	{
		// escaped once for the query string syntax, then again for the ES|QL string
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.BackslashTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`a\b` IS NOT NULL AND QSTR("a\\\\b:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldWithASlash_EscapesTheSlash()
	{
		// an unescaped '/' would open a regular expression
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.SlashTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`a/b` IS NOT NULL AND QSTR("a\\/b:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldWithParentheses_EscapesThem()
	{
		// unescaped parentheses would group
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.ParenthesizedTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`(p)` IS NOT NULL AND QSTR("\\(p\\):wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldWithAPlus_EscapesThePlus()
	{
		// an unescaped '+' would read as a required operator
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.PlusTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`x+y` IS NOT NULL AND QSTR("x\\+y:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldWithAQuote_EscapesItForBothSyntaxes()
	{
		// the query string syntax escapes the quote with a backslash, and the ES|QL string escapes both
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.QuoteTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`q"q` IS NOT NULL AND QSTR("q\\\"q:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldNamedLowercaseAnd_NamesItBare()
	{
		// the query string syntax reads only an uppercase AND as its operator
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.LowercaseAndTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`and` IS NOT NULL AND QSTR("and:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyEndsWithOverAHyphenatedField_EscapesTheName()
	{
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.HyphenatedTags.Any(t => t.EndsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`user-tags` IS NOT NULL AND QSTR("user\\-tags:*wat", {"allow_leading_wildcard": true}))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyContainingATextOverAHyphenatedField_EscapesTheName()
	{
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.HyphenatedTags.Any(t => t.Contains("wat")))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`user-tags` IS NOT NULL AND QSTR("user\\-tags:*wat*", {"allow_leading_wildcard": true}))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithMixedCase_KeepsTheCase()
	{
		// the pattern keeps the case it is given; whether the field compares it depends on its mapping
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith("Wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:Wat*"))
            """.NativeLineEndings());
	}

	[Test]
	[Arguments("a\nb", "a\\\\\\nb*")]
	[Arguments("a/b", "a\\\\/b*")]
	[Arguments("a?b", "a\\\\?b*")]
	[Arguments("[x", "\\\\[x*")]
	[Arguments("-x", "\\\\-x*")]
	[Arguments("+x", "\\\\+x*")]
	[Arguments("AND", "AND*")]
	[Arguments("😀", "😀*")]
	[Arguments("", "*")]
	public void Where_AnyStartsWithAnOddValue_EscapesItForBothSyntaxes(string prefix, string expected)
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			$"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:{expected}"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithAChar_TranslatesToQstr()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith('w')))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:w*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyEndsWithAChar_TranslatesToQstr()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.EndsWith('l')))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:*l", {"allow_leading_wildcard": true}))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyContainingAChar_TranslatesToQstr()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.Contains('a')))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:*a*", {"allow_leading_wildcard": true}))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyContainingACharOrdinally_TranslatesToQstr()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.Contains('a', StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:*a*", {"allow_leading_wildcard": true}))
            """.NativeLineEndings());
	}

	[Test]
	[Arguments(StringComparison.OrdinalIgnoreCase)]
	[Arguments(StringComparison.CurrentCulture)]
	public void Where_AnyContainingACharNotOrdinally_ThrowsNotSupported(StringComparison comparison)
	{
		// as for a string: ES|QL string matching is ordinal and case-sensitive
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.Contains('a', comparison)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*other than StringComparison.Ordinal*");
	}

	[Test]
	public void Where_AnyContainingAReservedCharOrdinally_EscapesIt()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.Contains(':', StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:*\\:*", {"allow_leading_wildcard": true}))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyContainingACapturedCharOrdinally_StaysInline()
	{
		// QSTR takes its query as a literal
		var letter = 'a';

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.Contains(letter, StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:*a*", {"allow_leading_wildcard": true}))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AllContainingACharOrdinally_ThrowsNotSupported()
	{
		// as for a string: QSTR answers whether some value passes the test, not whether every value does
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.All(t => t.Contains('a', StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*every value of tags must pass*Take(n)*");
	}

	[Test]
	public void Where_AnyContainingAReservedChar_EscapesIt()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.Contains(':')))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:*\\:*", {"allow_leading_wildcard": true}))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverANestedObjectWithAHyphen_EscapesThatSegment()
	{
		// the path is built from the object and its member, each escaped for the query string on its own
		var esql = CreateQuery<OddlyNestedTaggedProduct>()
			.From("products")
			.Where(p => p.Agent.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`user-agent`.tags IS NOT NULL AND QSTR("user\\-agent.tags:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverANestedObjectWithTwoOddSegments_EscapesBoth()
	{
		// both segments need escaping, and the dot between them stays a path separator
		var esql = CreateQuery<OddlyNestedTaggedProduct>()
			.From("products")
			.Where(p => p.Agent.OsNames.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`user-agent`.`os name` IS NOT NULL AND QSTR("user\\-agent.os\\ name:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldStartingWithAnAt_NamesItBare()
	{
		// an @ is no part of the query string syntax, as in @timestamp
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.AtTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (@tags IS NOT NULL AND QSTR("@tags:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldNamedByAKebabCasePolicy_EscapesTheName()
	{
		// the name comes from the naming policy, with no attribute to show that it needs escaping
		var esql = new EsqlQueryable<PolicyNamedTaggedProduct>(new EsqlQueryProvider(new JsonSerializerOptions
		{
			TypeInfoResolver = EsqlTestMappingContext.Default,
			PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower
		}))
			.From("products")
			.Where(p => p.UserTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`user-tags` IS NOT NULL AND QSTR("user\\-tags:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithAGreaterThan_EscapesItSoItIsNoRange()
	{
		// a bare leading > would read as a range, "greater than x"
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith(">x", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:\\>x*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithALessThan_EscapesItSoItIsNoRange()
	{
		// a bare leading < would read as a range, "less than x"
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith("<x", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("tags:\\<x*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldWithANoBreakSpace_EscapesIt()
	{
		// a no-break space is whitespace to the query string syntax as well
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.NoBreakSpaceTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`nb sp` IS NOT NULL AND QSTR("nb\\ sp:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldStartingWithAMinus_EscapesIt()
	{
		// an unescaped leading - would exclude the clause rather than name the field
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.LeadingMinusTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`-lead` IS NOT NULL AND QSTR("\\-lead:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAFieldNamedExists_NamesItBare()
	{
		// _exists_ is a name of the query string syntax, and a field of that name is still read as itself
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.ExistsTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (_exists_ IS NOT NULL AND QSTR("_exists_:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAMultiFieldOfAHyphenatedField_EscapesTheParent()
	{
		// the parent needs escaping and the subfield does not
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => p.HyphenatedTags.MultiField("keyword").Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`user-tags`.keyword IS NOT NULL AND QSTR("user\\-tags.keyword:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_NegatedAnyStartsWithOverAHyphenatedField_EscapesTheName()
	{
		// under NOT a field QSTR cannot find would turn no match into every document
		var esql = CreateQuery<OddlyNamedTaggedProduct>()
			.From("products")
			.Where(p => !p.HyphenatedTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE NOT (`user-tags` IS NOT NULL AND QSTR("user\\-tags:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyStartsWithOverAnOperatorInsideAPath_EscapesALetterOfThatSegment()
	{
		// every segment named AND, OR or NOT has a letter escaped, wherever it sits in the path
		var esql = CreateQuery<OddlyNestedTaggedProduct>()
			.From("products")
			.Where(p => p.Agent.AndTags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (`user-agent`.`AND` IS NOT NULL AND QSTR("user\\-agent.\\AND:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	[Arguments('*', "tags:\\\\**")]
	[Arguments('?', "tags:\\\\?*")]
	[Arguments('"', "tags:\\\\\\\"*")]
	[Arguments('\\', "tags:\\\\\\\\*")]
	[Arguments('-', "tags:\\\\-*")]
	[Arguments(' ', "tags:\\\\ *")]
	public void Where_AnyStartsWithAReservedChar_EscapesItForBothSyntaxes(char reserved, string expected)
	{
		// escaped once for the query string syntax, then again for the ES|QL string
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith(reserved)))
			.ToString();

		_ = esql.Should().Be(
			$$"""
            FROM products
            | WHERE (tags IS NOT NULL AND QSTR("{{expected}}"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_NegatedAnyStartsWithAChar_StaysDefinite()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => !p.Tags.Any(t => t.StartsWith('w')))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE NOT (tags IS NOT NULL AND QSTR("tags:w*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyNotStartingWithAChar_ThrowsNotSupported()
	{
		// as for a string: some value failing the test is not every value passing it, which QSTR cannot answer
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => !t.StartsWith('w')));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*Take(n)*");
	}

	[Test]
	public void Where_AllStartsWithAChar_ThrowsNotSupported()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.All(t => t.StartsWith('w')));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*Take(n)*");
	}

	[Test]
	public void Where_AnyEndsWith_AllowsTheLeadingWildcardItNeeds()
	{
		// a cluster may disallow leading wildcards in query strings, which would fail the query
		// only when it runs; the option allows it for this one query whatever the node setting
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.EndsWith("al", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Contain("""QSTR("tags:*al", {"allow_leading_wildcard": true})""");
	}

	[Test]
	public void Where_AnyStartsWith_NeedsNoOption()
	{
		// the wildcard trails, which every cluster allows
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Contain("""QSTR("tags:wat*"))""");
	}

	[Test]
	public void Where_AnyStartsWithNull_ThrowsNotSupported()
	{
		// "abc".StartsWith(null) throws, and a stored value is never null
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith(null!)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*tags with null*");
	}

	[Test]
	public void Where_TakeContainingNull_ThrowsNotSupported()
	{
		// under Take(n) LOCATE over null would answer false rather than fail as C# does
		var nothing = (string?)null;

		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Take(2).Any(t => t.Contains(nothing!)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*tags with null*");
	}

	[Test]
	public void Where_AnyStartsWithACapturedText_ReadsItOnce()
	{
		// the null check and QSTR share the value read, as every captured value is read once
		var holder = new CountingValue<string>("wat");

		_ = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith(holder.Value, StringComparison.Ordinal)))
			.ToString();

		_ = holder.Reads.Should().Be(1);
	}

	[Test]
	public void Where_AnyStartsWithACapturedChar_ReadsItOnce()
	{
		// as for a string
		var holder = new CountingValue<char>('w');

		_ = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith(holder.Value)))
			.ToString();

		_ = holder.Reads.Should().Be(1);
	}
}
