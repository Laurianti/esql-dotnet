// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using Elastic.Esql.Translation;

namespace Elastic.Esql.Tests.Translation;

/// <summary>
/// The commands of a raw fragment, split at the pipes that separate them and at no pipe that a
/// string or a quoted name holds.
/// </summary>
public class SplitCommandsTests
{
	[Test]
	public void SplitCommands_OneCommand_IsReturnedWhole() =>
		_ = "WHERE a > 1".SplitCommands().Should().Equal("WHERE a > 1");

	[Test]
	public void SplitCommands_CommandsJoinedWithPipes_AreSplit() =>
		_ = "WHERE a > 1 | KEEP a|LIMIT 5".SplitCommands().Should().Equal("WHERE a > 1 ", " KEEP a", "LIMIT 5");

	[Test]
	public void SplitCommands_APipeInAString_IsPartOfIt() =>
		_ = "WHERE a == \"x|y\" | KEEP a".SplitCommands().Should().Equal("WHERE a == \"x|y\" ", " KEEP a");

	[Test]
	public void SplitCommands_AnEscapedQuoteInAString_DoesNotEndIt() =>
		_ = "WHERE a == \"x\\\"|y\" | KEEP a".SplitCommands().Should().Equal("WHERE a == \"x\\\"|y\" ", " KEEP a");

	[Test]
	public void SplitCommands_APipeInATripleQuotedString_IsPartOfIt() =>
		_ = "WHERE a == \"\"\"x \"|\" y\"\"\" | KEEP a".SplitCommands().Should().Equal("WHERE a == \"\"\"x \"|\" y\"\"\" ", " KEEP a");

	[Test]
	public void SplitCommands_APipeInAQuotedName_IsPartOfIt() =>
		_ = "WHERE `a|b` IS NULL | KEEP a".SplitCommands().Should().Equal("WHERE `a|b` IS NULL ", " KEEP a");

	[Test]
	public void SplitCommands_ADoubledBacktickInAQuotedName_KeepsTheNameOpen() =>
		_ = "WHERE `a``|b` IS NULL | KEEP a".SplitCommands().Should().Equal("WHERE `a``|b` IS NULL ", " KEEP a");

	[Test]
	[Arguments("WHERE a == \"x|y")]
	[Arguments("WHERE a == \"\"\"x|y")]
	[Arguments("WHERE `a|b")]
	public void SplitCommands_AnUnclosedStringOrName_RunsToTheEnd(string fragment) =>
		_ = fragment.SplitCommands().Should().Equal(fragment);
}
