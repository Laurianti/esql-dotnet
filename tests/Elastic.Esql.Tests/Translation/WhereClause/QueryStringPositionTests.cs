// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

namespace Elastic.Esql.Tests.Translation.WhereClause;

/// <summary>
/// Where QSTR may sit in the pipeline. Elasticsearch allows it after FROM, WHERE and SORT
/// only, a stricter rule than the one for MATCH, which is refused after FORK, LIMIT and STATS
/// alone: any other command before it is refused when the query is translated rather than
/// failing when it runs.
/// </summary>
public class QueryStringPositionTests : EsqlTestBase
{
	[Test]
	public void Where_QstrAfterOrderBy_Translates()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.OrderBy(p => p.Name)
			.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | SORT name
            | WHERE (tags IS NOT NULL AND QSTR("tags:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_QstrAfterAnotherWhere_Translates()
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Where(p => p.Name != "x")
			.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | WHERE name != "x"
            | WHERE (tags IS NOT NULL AND QSTR("tags:wat*"))
            """.NativeLineEndings());
	}

	[Test]
	[Arguments("SORT name")]
	[Arguments("WHERE name != \"x\"")]
	[Arguments("where name != \"x\" | sort name")]
	public void Where_QstrAfterARawSortOrWhere_Translates(string fragment)
	{
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.RawEsql(fragment)
			.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)))
			.ToString();

		_ = esql.Should().EndWith("| WHERE (tags IS NOT NULL AND QSTR(\"tags:wat*\"))");
	}

	[Test]
	public void Where_QstrAfterKeep_ThrowsNotSupported()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Keep("name", "tags")
			.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*after KEEP: it translates to QSTR*");
	}

	[Test]
	public void Where_QstrAfterDrop_ThrowsNotSupported()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Drop("categories")
			.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*after DROP: it translates to QSTR*");
	}

	[Test]
	public void Where_QstrAfterAProjection_ThrowsNotSupported()
	{
		// a Select keeps, renames or computes the columns, and QSTR is allowed after none of them
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Select(p => new { p.Name, p.Tags })
			.Where(x => x.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*it translates to QSTR*");
	}

	[Test]
	public void Where_QstrAfterCompletion_ThrowsNotSupported()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Completion("summarize", "my-endpoint")
			.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*after COMPLETION: it translates to QSTR*");
	}

	[Test]
	[Arguments("EVAL x = 1", "EVAL")]
	[Arguments("RENAME categories AS c", "RENAME")]
	[Arguments("MV_EXPAND categories", "MV_EXPAND")]
	[Arguments("DISSECT name \"%{a}\"", "DISSECT")]
	[Arguments("GROK name \"%{WORD:w}\"", "GROK")]
	[Arguments("LOOKUP JOIN lk ON name", "LOOKUP")]
	[Arguments("SAMPLE 0.5", "SAMPLE")]
	[Arguments("where true | keep name, tags", "KEEP")]
	public void Where_QstrAfterARawCommand_ThrowsNotSupported(string fragment, string command)
	{
		// only WHERE and SORT may precede QSTR; a fragment is read command by command
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.RawEsql(fragment)
			.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage($"*after {command}: it translates to QSTR*");
	}

	[Test]
	public void Where_QstrInAForkBranchAfterKeep_ThrowsNotSupported()
	{
		// a branch is verified on top of the pipeline before the FORK
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Keep("name", "tags")
			.Fork(
				b => b.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal))),
				b => b.Take(5));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*after KEEP: it translates to QSTR*");
	}

	[Test]
	public void Where_MatchAfterKeep_StillTranslates()
	{
		// the stricter rule is QSTR's own: MATCH is allowed after KEEP
		var esql = CreateQuery<TaggedProduct>()
			.From("products")
			.Keep("name", "tags")
			.Where(p => p.Tags.Contains("water"))
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM products
            | KEEP name, tags
            | WHERE (tags IS NOT NULL AND MATCH(tags, "water"))
            """.NativeLineEndings());
	}

	[Test]
	public void Where_QstrAfterTake_ThrowsNotSupported()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Take(10)
			.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*after LIMIT: it translates to QSTR*");
	}

	[Test]
	public void Where_QstrAfterGroupBy_ThrowsNotSupported()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.GroupBy(p => p.Name)
			.Select(g => new { Name = g.Key, Tags = EsqlFunctions.Values(g, p => p.Name) })
			.Where(r => r.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*after STATS: it translates to QSTR*");
	}

	[Test]
	public void Where_QstrAfterFork_ThrowsNotSupported()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Fork(b => b.Where(p => p.Name != "x"), b => b.Where(p => p.Name != "y"))
			.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*after FORK: it translates to QSTR*");
	}

	[Test]
	public void Where_QstrAfterAComputedMember_ThrowsNotSupported()
	{
		// a member computed by the projection is an EVAL
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Select(p => new { p.Tags, Upper = p.Name.ToUpperInvariant() })
			.Where(x => x.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*after EVAL: it translates to QSTR*");
	}

	[Test]
	public void Where_QstrAfterARenamedMember_ThrowsNotSupported()
	{
		// a member the projection names anew is a RENAME
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.Select(p => new { Labels = p.Tags })
			.Where(x => x.Labels.Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*after RENAME: it translates to QSTR*");
	}

	[Test]
	public void Where_QstrAfterLookupJoin_ThrowsNotSupported()
	{
		var query = CreateQuery<TaggedProduct>()
			.From("products")
			.LookupJoin<TaggedProduct, LanguageLookup, TaggedProduct>(
				"languages_lookup",
				(outer, inner) => outer.Name == inner.LanguageName,
				(outer, inner) => outer)
			.Where(p => p.Tags.Any(t => t.StartsWith("wat", StringComparison.Ordinal)));

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>().WithMessage("*after LOOKUP JOIN: it translates to QSTR*");
	}
}
