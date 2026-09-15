// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

namespace Elastic.Esql.Tests.Translation.Projections;

/// <summary>
/// Projections of the shape a GraphQL layer emits for a nested selection:
/// <c>param == null ? null : new Child { Field = param.Child.Field }</c>.
/// Without the null guard this already worked; with it, it did not. The guard
/// only stands for the branch when the branch reads through the guarded path.
/// </summary>
public class NullGuardedNestedProjectionTests : EsqlTestBase
{
	[Test]
	public void NullGuardedNestedInit_ProjectsTheInnerField()
	{
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null! : new NestedSelectionHost { Name = l.Host.Name }
			})
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | KEEP host.name
            """.NativeLineEndings());
	}

	[Test]
	public void PlainNestedInit_StillWorks()
	{
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = new NestedSelectionHost { Name = l.Host.Name }
			})
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | KEEP host.name
            """.NativeLineEndings());
	}

	[Test]
	public void AGuardOverAnUnrelatedBranch_KeepsItsOwnCondition()
	{
		// the guard tests Host, the branch reads Message: the two are unrelated, so the
		// guard cannot be dropped or the emitted condition would test the wrong field
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new { Value = l.Host == null ? null : l.Message })
			.ToString();

		_ = esql.Should().Contain("host");
	}

	[Test]
	public void AGuardOverAPurelyConstantChild_IsNotUnwrapped()
	{
		// nothing in the child reads through Host, so dropping the guard would give the
		// child a value for a document that has no Host at all
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null! : new NestedSelectionHost { Name = "constant" }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}
}
