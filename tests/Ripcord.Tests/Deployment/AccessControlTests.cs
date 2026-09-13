using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// Reading `icacls` output. The adapter runs the command and cannot be executed off Windows;
/// what its answer means is decided in the Domain, which is why this can be a test.
public class AccessControlTests
{
    private const string Account = @"NT SERVICE\ripcord";

    /// The shape icacls prints: the path, then one entry per line, the first sharing the line
    /// with the path.
    private const string Granted =
        "D:\\Ripcord\\state.json NT SERVICE\\ripcord:(R)\n"
        + "                      BUILTIN\\Administrators:(F)\n"
        + "\n"
        + "Successfully processed 1 files; Failed processing 0 files\n";

    [Fact]
    public void An_account_granted_read_has_read() =>
        Assert.True(AccessControl.GrantsRead(Granted, Account));

    /// The case a substring search gets wrong, and the reason this is not a substring search:
    /// the entry that denies the account also names it.
    [Fact]
    public void An_account_denied_read_does_not_have_it() =>
        Assert.False(AccessControl.GrantsRead(
            "D:\\Ripcord\\state.json NT SERVICE\\ripcord:(DENY)(R)\n", Account));

    /// Deny wins over a grant elsewhere in the list, as it does in Windows itself.
    [Fact]
    public void A_deny_outranks_a_grant_for_the_same_account() =>
        Assert.False(AccessControl.GrantsRead(
            "D:\\Ripcord\\state.json NT SERVICE\\ripcord:(R)\n"
            + "                      NT SERVICE\\ripcord:(DENY)(R)\n",
            Account));

    [Theory]
    [InlineData("(F)")]
    [InlineData("(M)")]
    [InlineData("(RX)")]
    [InlineData("(GR)")]
    [InlineData("(RD,REA,X)")]
    public void Every_form_of_read_counts(string rights) =>
        Assert.True(AccessControl.GrantsRead($"state.json NT SERVICE\\ripcord:{rights}\n", Account));

    /// Write is not read. A right is matched as a whole token for exactly this reason.
    [Theory]
    [InlineData("(W)")]
    [InlineData("(WD,AD)")]
    public void A_right_that_is_not_read_does_not_count(string rights) =>
        Assert.False(AccessControl.GrantsRead($"state.json NT SERVICE\\ripcord:{rights}\n", Account));

    [Fact]
    public void An_account_the_list_does_not_name_has_nothing() =>
        Assert.False(AccessControl.GrantsRead(
            "D:\\Ripcord\\state.json BUILTIN\\Administrators:(F)\n", Account));

    /// No output at all is the same answer as no entry: grant it, which changes nothing if it
    /// was already there.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_to_read_is_no_access(string? output) =>
        Assert.False(AccessControl.GrantsRead(output, Account));

    /// Windows account names are case-insensitive, and icacls prints them as they were
    /// registered rather than as the configuration spells them.
    [Fact]
    public void The_account_is_matched_whatever_its_case() =>
        Assert.True(AccessControl.GrantsRead(
            "state.json nt service\\RIPCORD:(R)\n", Account));
}
