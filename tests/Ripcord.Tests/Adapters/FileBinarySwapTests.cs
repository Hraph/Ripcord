using System.Text;
using Ripcord.Adapters.Update;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;

namespace Ripcord.Tests.Adapters;

/// The file moves, against a real filesystem. Only two things about this adapter are
/// Windows-specific — that a running image can be renamed but not overwritten, and whether the
/// service account may write beside the binary — and neither is a move. Everything else is
/// exercised here rather than discovered on a host during an incident.
public sealed class FileBinarySwapTests : IDisposable
{
    private static readonly byte[] NewBinary = Encoding.UTF8.GetBytes("the new ripcord");

    private static readonly byte[] OldBinary = Encoding.UTF8.GetBytes("the running ripcord");

    private readonly string folder = Directory.CreateTempSubdirectory("ripcord-swap").FullName;

    private readonly FileBinarySwap swap = new();

    private string Binary => Path.Combine(this.folder, "ripcord.exe");

    private string Staged => this.Binary + ".new";

    private string Previous => this.Binary + ".old";

    public void Dispose() => Directory.Delete(this.folder, recursive: true);

    [Fact]
    public void A_bare_host_has_nothing_staged_and_nothing_kept()
    {
        this.Running();

        Assert.Equal(new StagedBinaries(false, false), this.swap.Observe(this.Binary));
    }

    [Fact]
    public void What_an_earlier_run_left_behind_is_reported()
    {
        this.Running();
        File.WriteAllBytes(this.Staged, NewBinary);
        File.WriteAllBytes(this.Previous, OldBinary);

        Assert.Equal(new StagedBinaries(true, true), this.swap.Observe(this.Binary));
    }

    /// The running binary is kept, not overwritten. It is what this host goes back to.
    [Fact]
    public void Setting_aside_keeps_the_running_binary_and_stages_the_new_one()
    {
        this.Running();

        this.Apply(UpdateAction.SetAside);

        Assert.False(File.Exists(this.Binary));
        Assert.Equal(OldBinary, File.ReadAllBytes(this.Previous));
        Assert.Equal(NewBinary, File.ReadAllBytes(this.Staged));
    }

    [Fact]
    public void Installing_puts_the_new_binary_where_the_running_one_was()
    {
        this.Running();

        this.Apply(UpdateAction.SetAside);
        this.Apply(UpdateAction.Install);

        Assert.Equal(NewBinary, File.ReadAllBytes(this.Binary));
        Assert.False(File.Exists(this.Staged));
        Assert.Equal(OldBinary, File.ReadAllBytes(this.Previous));
    }

    [Fact]
    public void Discarding_removes_only_what_an_earlier_update_kept()
    {
        this.Running();
        File.WriteAllBytes(this.Previous, OldBinary);

        this.Apply(UpdateAction.DiscardPrevious);

        Assert.False(File.Exists(this.Previous));
        Assert.True(File.Exists(this.Binary));
    }

    [Fact]
    public void Discarding_nothing_is_not_a_failure()
    {
        this.Running();

        this.Apply(UpdateAction.DiscardPrevious);

        Assert.True(File.Exists(this.Binary));
    }

    /// The state the rollback exists for: aside, and not yet replaced. This host currently has
    /// no `ripcord.exe` at all.
    [Fact]
    public void Restoring_puts_the_running_binary_back_after_a_failed_install()
    {
        this.Running();
        this.Apply(UpdateAction.SetAside);

        this.swap.Restore(this.Binary);

        Assert.Equal(OldBinary, File.ReadAllBytes(this.Binary));
    }

    /// Keeping it would offer the next run a release that nothing re-verified.
    [Fact]
    public void Restoring_throws_away_the_staged_release()
    {
        this.Running();
        this.Apply(UpdateAction.SetAside);

        this.swap.Restore(this.Binary);

        Assert.False(File.Exists(this.Staged));
    }

    /// Restore runs when the sequence has already failed, so it has to cope with the install
    /// having actually worked — putting the old binary back over a good new one would undo a
    /// success.
    [Fact]
    public void Restoring_leaves_an_installed_binary_alone()
    {
        this.Running();
        this.Apply(UpdateAction.SetAside);
        this.Apply(UpdateAction.Install);

        this.swap.Restore(this.Binary);

        Assert.Equal(NewBinary, File.ReadAllBytes(this.Binary));
    }

    /// A download interrupted half way must never leave something that looks like a staged
    /// release: it is written under another name and moved into place whole.
    [Fact]
    public void Nothing_partial_is_left_beside_the_binary()
    {
        this.Running();

        this.Apply(UpdateAction.SetAside);

        Assert.Empty(Directory.GetFiles(this.folder, "*.partial"));
    }

    private void Running() => File.WriteAllBytes(this.Binary, OldBinary);

    private void Apply(UpdateAction action) =>
        this.swap.Apply(
            new UpdateStep(1, action, action.ToString(), "because the test says so"),
            new StagedRelease(this.Binary, NewBinary, "0.2.0"),
            CancellationToken.None);
}
