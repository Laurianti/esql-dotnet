// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

namespace Elastic.Esql.Tests.Translation.WhereClause;

/// <summary>
/// Predicates over multi-value document fields. A document matches when any of the
/// field's values matches, which is what MATCH does, and unlike MV_EXPAND it does not
/// duplicate rows.
/// </summary>
public class MultiValueFieldTests : EsqlTestBase
{
	[Test]
	public void Where_AnyWithEquality_TranslatesToMatch()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t == "water"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE MATCH(tags, "water")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyWithReversedEquality_TranslatesToMatch()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => "water" == t))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE MATCH(tags, "water")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_ContainsOnAFieldCollection_TranslatesToMatch()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Contains("water"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE MATCH(tags, "water")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyWithoutPredicate_TranslatesToMvCount()
	{
		// the count is coalesced: a missing field is an empty sequence, where Any() is
		// false, and so is its negation's opposite
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any())
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE COALESCE(MV_COUNT(tags), 0) > 0
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyOnAListProperty_TranslatesToMatch()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Categories.Any(c => c == "pumps"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE MATCH(categories, "pumps")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyOnANumericList_TranslatesToMvMax()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.Any(r => r == 42))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE MATCH(ratings, 42)
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TwoAnyPredicates_CombineWithOr()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t == "water") || p.Tags.Any(t => t == "iot"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (MATCH(tags, "water") OR MATCH(tags, "iot"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_TwoAnyPredicates_CombineWithAnd()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t == "iot") && p.Tags.Any(t => t == "industrial"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (MATCH(tags, "iot") AND MATCH(tags, "industrial"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_NegatedAny_TranslatesToNotMatch()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => !p.Tags.Any(t => t == "iot"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE NOT MATCH(tags, "iot")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyCombinedWithAScalarPredicate_KeepsBoth()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t == "water") && p.Name == "pump")
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (MATCH(tags, "water") AND name == "pump")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyWithACapturedValue_Parameterizes()
	{
		var tag = "water";

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t == tag))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE MATCH(tags, "water")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_ContainsOverAConstantCollection_StillTranslatesToIn()
	{
		// the existing behaviour must not change: here the collection is the constant,
		// and the document field is the argument
		var names = new[] { "a", "b" };

		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => names.Contains(p.Name))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE name IN ("a", "b")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AllWithEquality_RequiresASingleMatchingValue()
	{
		// a missing field has no value that differs, as All() over an empty sequence is true
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.All(t => t == "iot"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (tags IS NULL OR (MV_COUNT(MV_DEDUPE(tags)) == 1 AND MATCH(tags, "iot")))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AllOnANumericList_TranslatesToMvMin()
	{
		// a field holding [42, 42] has every value equal to 42, so it must match:
		// the count has to be over the distinct values, not the raw ones, because
		// only keyword fields are deduplicated by the index
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Ratings.All(r => r == 42))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE (ratings IS NULL OR (MV_COUNT(MV_DEDUPE(ratings)) == 1 AND MATCH(ratings, 42)))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AllWithInequality_TranslatesToNotMatch()
	{
		// "every value differs from x" is "none of the values is x"
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.All(t => t != "iot"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE NOT MATCH(tags, "iot")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AllCombinedWithAScalarPredicate_KeepsBoth()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.All(t => t == "iot") && p.Name == "sensor")
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE ((tags IS NULL OR (MV_COUNT(MV_DEDUPE(tags)) == 1 AND MATCH(tags, "iot"))) AND name == "sensor")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyOverASet_TranslatesToMatch()
	{
		// no collection instance exists at translation time, so a set's comparer is as
		// invisible as a StringComparison on a scalar: the store's equality decides
		var esql = CreateQuery<SetTaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t == "iot"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE MATCH(tags, "iot")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_ContainsOverASet_TranslatesToMatch()
	{
		var esql = CreateQuery<SetTaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Contains("iot"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE MATCH(tags, "iot")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_ContainsOverAnInterfaceTypedField_TranslatesToMatch()
	{
		var esql = CreateQuery<InterfaceTaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Contains("iot"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE MATCH(tags, "iot")
            """.NativeLineEndings());
	}

	[Test]
	public void Where_AnyWithAPerValuePredicate_ThrowsNotSupported()
	{
		// StartsWith holds for one value at a time, which needs the field read position by
		// position: that is the next part
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Tags.Any(t => t.StartsWith("wat")));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*individual values*");
	}
}
