using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// The three things the publishing service's privileges rest on, decided off Windows: who is
/// in a local group, which SID a virtual account has, and one ACE on a WMI namespace.
public class PublisherPrivilegeTests
{
    private const string Account = @"NT SERVICE\ripcord-publish";

    private const string English = """
        Alias name     Hyper-V Administrators
        Comment        Members of this group have complete and unrestricted access to all features of Hyper-V.

        Members

        -------------------------------------------------------------------------------
        NT SERVICE\ripcord-publish
        The command completed successfully.
        """;

    private const string French = """
        Nom d'alias    Administrateurs Hyper-V
        Commentaire    Les membres de ce groupe disposent d'un accès complet.

        Membres

        -------------------------------------------------------------------------------
        NT SERVICE\ripcord
        La commande s'est terminée correctement.
        """;

    [Fact]
    public void A_member_is_found_whatever_the_language_and_only_by_its_whole_name()
    {
        Assert.True(LocalGroup.HasMember(English, Account));
        Assert.True(LocalGroup.HasMember(English.ReplaceLineEndings("\r\n"), Account.ToLowerInvariant()));
        Assert.False(LocalGroup.HasMember(French, Account));
        Assert.False(LocalGroup.HasMember(English, @"NT SERVICE\ripcord"));
        Assert.False(LocalGroup.HasMember(null, Account));
    }

    [Theory]
    [InlineData(true, "System error 1378 has occurred.", true)]
    [InlineData(false, "Erreur système 1377.", true)]
    [InlineData(true, "Erreur système 1377.", false)]
    [InlineData(true, "System error 5 has occurred.", false)]
    public void Asking_for_what_is_already_so_leaves_nothing_to_do(bool adding, string output, bool nothing) =>
        Assert.Equal(nothing, LocalGroup.LeavesNothingToDo(adding, output));

    /// The formula's known answer: Windows' own TrustedInstaller.
    [Fact]
    public void A_virtual_account_sid_is_the_one_windows_derives() =>
        Assert.Equal(
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
            VirtualAccount.Sid("TrustedInstaller"));

    private const string Sid = "S-1-5-80-1-2-3-4-5";

    private const string Namespace =
        "O:BAG:SYD:(A;CI;CCDCLCSWRPWPRCWD;;;BA)(A;CIID;CCDCRP;;;NS)(A;CIID;CCDCRP;;;LS)";

    [Fact]
    public void The_grant_is_one_ace_placed_before_the_inherited_ones()
    {
        NamespaceEdit edit = NamespaceAcl.WithGrant(Namespace, Sid);

        Assert.Equal(
            $"O:BAG:SYD:(A;CI;CCDCLCSWRPWPRCWD;;;BA)(A;;CCDC;;;{Sid})(A;CIID;CCDCRP;;;NS)(A;CIID;CCDCRP;;;LS)",
            edit.Sddl);
        Assert.Equal(NamespaceGrant.Granted, NamespaceAcl.Read(edit.Sddl!, Sid).State);
        Assert.Equal(edit.Sddl, NamespaceAcl.WithGrant(edit.Sddl!, Sid).Sddl);
    }

    [Fact]
    public void Removing_takes_back_exactly_that_ace_and_leaves_an_operators_own()
    {
        string granted = NamespaceAcl.WithGrant(Namespace, Sid).Sddl!;
        string withOperators = granted.Replace("(A;CIID;CCDCRP;;;NS)", $"(A;;CCDCRP;;;{Sid})(A;CIID;CCDCRP;;;NS)", StringComparison.Ordinal);

        Assert.Equal(Namespace, NamespaceAcl.WithoutGrant(granted, Sid).Sddl);
        Assert.Contains($"(A;;CCDCRP;;;{Sid})", NamespaceAcl.WithoutGrant(withOperators, Sid).Sddl, StringComparison.Ordinal);
        Assert.Equal(Namespace, NamespaceAcl.WithoutGrant(Namespace, Sid).Sddl);
    }

    [Theory]
    [InlineData("O:BAG:SYD:(D;;CCDC;;;S-1-5-80-1-2-3-4-5)(A;;CCDC;;;BA)", "denies")]
    [InlineData("O:BAG:SYD:(A;;CCDC;;;BA)S:(AU;SA;CC;;;WD)", "not one Ripcord edits")]
    [InlineData("O:BAG:SYD:NO_ACCESS_CONTROL", "not one Ripcord edits")]
    [InlineData("O:BAG:SYD:(XA;;CCDC;;;BA;(Member_of {SID(BA)}))", "not one Ripcord edits")]
    [InlineData("O:BAG:SY", "not one Ripcord edits")]
    [InlineData("O:BAG:SYD:(A;;CCDC;;BA)", "not one Ripcord edits")]
    public void Anything_it_does_not_fully_understand_is_left_alone(string sddl, string why)
    {
        NamespaceEdit edit = NamespaceAcl.WithGrant(sddl, Sid);

        Assert.Null(edit.Sddl);
        Assert.Contains(why, edit.Refusal, StringComparison.Ordinal);
    }
}
