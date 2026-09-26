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

    /// The listener's name is the start of the publisher's: an entry for one is never the other's.
    [Fact]
    public void The_publisher_entry_is_not_the_listeners()
    {
        const string State =
            "C:\\Ripcord\\state NT SERVICE\\ripcord-publish:(OI)(CI)(M)\n"
            + "                  BUILTIN\\Administrators:(OI)(CI)(F)\n";

        Assert.False(AccessControl.GrantsRead(State, Account));
        Assert.False(AccessControl.GrantsModify(State, Account));
        Assert.False(AccessControl.GrantsExplicitly(State, Account));
        Assert.True(AccessControl.GrantsModify(State, @"NT SERVICE\ripcord-publish"));
    }

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

    /// The logs folder needs modify: pruning an old log deletes it, which write alone does not
    /// allow. The inheritable form is what `service install` grants.
    [Theory]
    [InlineData("(OI)(CI)(M)")]
    [InlineData("(I)(OI)(CI)(M)")]
    [InlineData("(F)")]
    public void Modify_or_full_control_counts_as_modify(string rights) =>
        Assert.True(AccessControl.GrantsModify($"logs NT SERVICE\\ripcord:{rights}\n", Account));

    [Theory]
    [InlineData("(RX)")]
    [InlineData("(W)")]
    [InlineData("(OI)(CI)(R)")]
    public void Read_or_write_alone_is_not_modify(string rights) =>
        Assert.False(AccessControl.GrantsModify($"logs NT SERVICE\\ripcord:{rights}\n", Account));

    [Fact]
    public void A_deny_outranks_a_modify_grant() =>
        Assert.False(AccessControl.GrantsModify(
            "logs NT SERVICE\\ripcord:(OI)(CI)(M)\n"
            + "     NT SERVICE\\ripcord:(DENY)(W)\n",
            Account));

    [Fact]
    public void Another_account_holding_modify_gives_this_one_nothing() =>
        Assert.False(AccessControl.GrantsModify(
            "logs BUILTIN\\Administrators:(OI)(CI)(M)\n", Account));

    /// Only an entry of its own counts as granted by a deployment: an inherited one goes with
    /// its parent, and a deny is no grant at all.
    [Theory]
    [InlineData("C:\\Old NT SERVICE\\ripcord:(OI)(CI)(R)\n", true)]
    [InlineData("C:\\Old\\logs NT SERVICE\\ripcord:(I)(OI)(CI)(R)\n", false)]
    [InlineData("C:\\Old NT SERVICE\\ripcord:(DENY)(R)\n", false)]
    [InlineData("C:\\Old BUILTIN\\Users:(RX)\n", false)]
    public void An_explicit_entry_is_told_from_an_inherited_one(string output, bool expected) =>
        Assert.Equal(expected, AccessControl.GrantsExplicitly(output, Account));

    /// `icacls <folder>\\*` over the machine keys: several blocks, CRLF, a file the console
    /// could not open, and the summary line in the host's language.
    [Fact]
    public void The_key_files_granting_the_account_are_named()
    {
        const string Output =
            "C:\\ProgramData\\Microsoft\\Crypto\\Keys\\aaa_guid NT AUTHORITY\\SYSTEM:(F)\r\n"
            + "                                          NT SERVICE\\ripcord:(R)\r\n"
            + "\r\n"
            + "C:\\ProgramData\\Microsoft\\Crypto\\Keys\\bbb_guid NT AUTHORITY\\SYSTEM:(F)\r\n"
            + "                                          BUILTIN\\Administrators:(F)\r\n"
            + "\r\n"
            + "C:\\ProgramData\\Microsoft\\Crypto\\Keys\\ccc_guid: Acces refuse.\r\n"
            + "C:\\ProgramData\\Microsoft\\Crypto\\Keys\\ddd_guid NT SERVICE\\ripcord:(R)\r\n"
            + "\r\n"
            + "2 fichiers traites correctement ; echec du traitement de 1 fichiers\r\n";

        Assert.Equal(
            [
                "C:\\ProgramData\\Microsoft\\Crypto\\Keys\\aaa_guid",
                "C:\\ProgramData\\Microsoft\\Crypto\\Keys\\ddd_guid",
            ],
            AccessControl.FilesGrantingExplicitly(
                Output, "C:\\ProgramData\\Microsoft\\Crypto\\Keys", Account));
    }

    /// A folder with a space in it, and a block printed in another form than the folder given:
    /// that one is left out, and its entries are not taken for the previous file's.
    [Fact]
    public void Only_blocks_under_the_folder_listed_are_named()
    {
        const string Output =
            "D:\\Program Data\\Keys\\aaa_guid NT AUTHORITY\\SYSTEM:(F)\r\n"
            + "                             NT SERVICE\\ripcord:(R)\r\n"
            + "D:\\PROGRA~1\\Keys\\bbb_guid NT AUTHORITY\\SYSTEM:(F)\r\n"
            + "                          NT SERVICE\\ripcord:(R)\r\n";

        Assert.Equal(
            ["D:\\Program Data\\Keys\\aaa_guid"],
            AccessControl.FilesGrantingExplicitly(Output, "D:\\Program Data\\Keys\\", Account));
    }

    /// 0.7.0's snapshot grant reached every file below the install folder; the configuration
    /// grant stops at the folder's own files. Only the account's own entries count.
    [Theory]
    [InlineData("C:\\Ripcord NT SERVICE\\ripcord:(OI)(CI)(R)\n", true)]
    [InlineData("C:\\Ripcord NT SERVICE\\ripcord:(CI)(R)\n", true)]
    [InlineData("C:\\Ripcord NT SERVICE\\ripcord:(OI)(NP)(R)\n", false)]
    [InlineData("C:\\Ripcord NT SERVICE\\ripcord:(R)\n", false)]
    [InlineData("C:\\Ripcord NT SERVICE\\ripcord:(I)(OI)(CI)(R)\n", false)]
    [InlineData("C:\\Ripcord NT SERVICE\\ripcord-publish:(OI)(CI)(R)\n", false)]
    public void A_grant_reaching_below_the_folder_is_told_apart(string output, bool expected) =>
        Assert.Equal(expected, AccessControl.GrantsExplicitlyBelow(output, Account));
}
