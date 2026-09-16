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
	public void AGuardOverAnUnrelatedBranch_IsRefused()
	{
		// the guard tests Host, the branch reads Message: the two are unrelated, so the
		// guard cannot be folded away, and the general conditional fallback renders the
		// test with the C# operator rather than an ES|QL IS NULL
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new { Value = l.Host == null ? null : l.Message });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void AGuardOverAFunctionOfTheGuardedPath_IsUnwrapped()
	{
		// TRIM of a missing value is null, as every scalar function is over a null input,
		// so the branch is null exactly when the guard says so and the guard can go
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null! : new NestedSelectionHost { Name = EsqlFunctions.Trim(l.Host.Name) }
			})
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | EVAL host.name = TRIM(host.name)
            | KEEP host.name
            """.NativeLineEndings());
	}

	[Test]
	public void AGuardOverAFunctionThatAnswersNull_IsRefused()
	{
		// COALESCE gives a missing value a value of its own, so for a document with no
		// Host the child would carry "x" where the source gives null
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null! : new NestedSelectionHost { Name = EsqlFunctions.Coalesce(l.Host.Name, "x") }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void AGuardOverAnAnonymousChild_IsUnwrapped()
	{
		// an anonymous child is built with new rather than an initializer: its arguments
		// are what has to read through the guarded path
		var esql = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new { Host = l.Host == null ? null : new { l.Host.Name } })
			.ToString();

		_ = esql.Should().Be(
			"""
            FROM logs-*
            | KEEP host.name
            """.NativeLineEndings());
	}

	[Test]
	public void AGuardOverAnAnonymousChildWithAConstant_IsRefused()
	{
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new { Host = l.Host == null ? null : new { l.Host.Name, Label = "constant" } });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void AGuardOnAProjectedValue_IsKeptUnlessTheBranchReadsThroughIt()
	{
		// after Select(n => n.Child) the parameter stands for the child, which may well be
		// null: a constant child would be emitted for it, where the source gives null
		var query = CreateQuery<TreeNode>()
			.From("nodes")
			.Select(n => n.Child)
			.Select(n => new { Wrap = n == null ? null : new { Label = "x" } });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void AGuardOnAProjectedValueReadThrough_IsUnwrapped()
	{
		var esql = CreateQuery<TreeNode>()
			.From("nodes")
			.Select(n => n.Child)
			.Select(n => new { Wrap = n == null ? null : new { n.Name } })
			.ToString();

		_ = esql.Should().Contain("RENAME name AS wrap.name");
	}

	[Test]
	public void AGuardOnTheDocumentRow_StillTakesAConstantChild()
	{
		// the document row is never null, so the guard on it is no guard at all and the
		// constant child is what the source gives
		var esql = CreateQuery<TreeNode>()
			.From("nodes")
			.Select(n => new { Wrap = n == null ? null : new { Label = "x" } })
			.ToString();

		_ = esql.Should().Contain("EVAL wrap.label = \"x\"");
	}

	[Test]
	public void AGuardOverASearchFunction_IsRefused()
	{
		// MATCH answers a missing field with a definite no rather than null, so the guard
		// cannot be dropped around it
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new { Host = l.Host == null ? null : new { Hit = EsqlFunctions.Match(l.Host.Name, "x") } });

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void AGuardOverAChildWithAConstructorArgument_IsRefused()
	{
		// the argument of the constructor is a member of the child as much as a binding
		// is: a constant there would be a value for a document without the parent
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new
			{
				Host = l.Host == null ? null : new NestedSelectionHostWithTag("constant") { Name = l.Host.Name }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void AGuardOverAChildWithANestedInitializer_IsRefused()
	{
		// "Geo = { City = ... }" is a binding that is not an assignment: nothing is read
		// into it, so it cannot be said to read through the guarded path
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null ? null! : new NestedSelectionHost { Name = l.Host.Name, Geo = { City = "constant" } }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
	}

	[Test]
	public void AGuardOverAChildWithAConstantMember_IsRefused()
	{
		// one member reads through Host, the other is a constant: for a document with
		// no Host the source gives null, where the constant would give a child with a
		// value in it, so the guard cannot be dropped
		var query = CreateQuery<NestedSelectionDocument>()
			.From("logs-*")
			.Select(l => new NestedSelectionDocument
			{
				Host = l.Host == null
					? null!
					: new NestedSelectionHost { Name = "constant", Geo = new NestedSelectionGeo { City = l.Host.Geo.City } }
			});

		var act = () => query.ToString();

		_ = act.Should().Throw<NotSupportedException>();
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
