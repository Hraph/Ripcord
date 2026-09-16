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

    /// What an earlier update left in `.old` — the version `ripcord rollback` goes back to.
    private static readonly byte[] KeptBinary = Encoding.UTF8.GetBytes("the ripcord before that");

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

        StagedBinaries staged = this.swap.Observe(this.Binary);

        Assert.False(staged.HasStagedDownload);
        Assert.False(staged.HasPreviousBinary);
        Assert.Null(staged.PreviousVersion);
        Assert.Null(staged.PreviousSetAsideAt);
    }

    [Fact]
    public void What_an_earlier_run_left_behind_is_reported()
    {
        this.Running();
        File.WriteAllBytes(this.Staged, NewBinary);
        File.WriteAllBytes(this.Previous, OldBinary);

        StagedBinaries staged = this.swap.Observe(this.Binary);

        Assert.True(staged.HasStagedDownload);
        Assert.True(staged.HasPreviousBinary);

        // Read off the file rather than remembered, so a binary replaced by hand still
        // answers and there is no state file to go stale.
        Assert.Equal(
            new DateTimeOffset(File.GetLastWriteTimeUtc(this.Previous), TimeSpan.Zero),
            staged.PreviousSetAsideAt);
    }

    /// A file that carries no version is a fact about the file, never a reason to refuse to go
    /// back to it — refusing would strand a host on the release it is retreating from.
    [Fact]
    public void A_kept_binary_carrying_no_version_is_reported_without_one()
    {
        this.Running();
        File.WriteAllBytes(this.Previous, OldBinary);

        Assert.Null(this.swap.Observe(this.Binary).PreviousVersion);
    }

    /// What `ripcord rollback` does: what was running is set aside, what was set aside runs.
    /// Running it again returns — going back is not a one-way door.
    [Fact]
    public void Going_back_exchanges_the_running_binary_with_the_one_kept()
    {
        this.Running();
        File.WriteAllBytes(this.Previous, KeptBinary);

        this.swap.SwapWithPrevious(this.Binary);

        Assert.Equal(KeptBinary, File.ReadAllBytes(this.Binary));
        Assert.Equal(OldBinary, File.ReadAllBytes(this.Previous));

        this.swap.SwapWithPrevious(this.Binary);

        Assert.Equal(OldBinary, File.ReadAllBytes(this.Binary));
    }

    /// Nothing is removed. A step that deleted a file would be a step that fails on Windows
    /// and nowhere else, which is the worst place to discover it.
    [Fact]
    public void Going_back_leaves_both_binaries_on_the_host()
    {
        this.Running();
        File.WriteAllBytes(this.Previous, KeptBinary);

        this.swap.SwapWithPrevious(this.Binary);

        Assert.Equal(
            ["ripcord.exe", "ripcord.exe.old"],
            Directory.GetFiles(this.folder).Select(Path.GetFileName).Order());
    }

    /// Everything that can fail for want of room or permission fails while the host is still
    /// whole: the kept binary is copied into place before anything is moved.
    [Fact]
    public void An_exchange_that_cannot_start_moves_nothing()
    {
        this.Running();

        // No binary kept aside, so the copy that opens the sequence has nothing to copy.
        Assert.Throws<FileNotFoundException>(() => this.swap.SwapWithPrevious(this.Binary));

        Assert.Equal(OldBinary, File.ReadAllBytes(this.Binary));
        Assert.False(File.Exists(this.Binary + ".incoming"));
    }

    /// The bug this guards: a `.swap` file holds the only copy of a binary this host was
    /// running, and the first move used to overwrite it. Two interrupted runs in a row would
    /// have destroyed it silently.
    [Fact]
    public void A_file_left_by_an_unfinished_exchange_is_never_written_over()
    {
        this.Running();
        File.WriteAllBytes(this.Previous, KeptBinary);
        File.WriteAllBytes(this.Binary + ".swap", Encoding.UTF8.GetBytes("the only copy"));

        Assert.Throws<IOException>(() => this.swap.SwapWithPrevious(this.Binary));

        Assert.Equal("the only copy", File.ReadAllText(this.Binary + ".swap"));
        Assert.Equal(OldBinary, File.ReadAllBytes(this.Binary));
    }

    /// And the next run sees it, so the plan can refuse instead of presenting a sequence that
    /// looks perfectly ordinary.
    [Fact]
    public void A_file_left_by_an_unfinished_exchange_is_reported()
    {
        this.Running();
        File.WriteAllBytes(this.Binary + ".swap", KeptBinary);

        Assert.True(this.swap.Observe(this.Binary).HasInterruptedSwap);
    }

    [Fact]
    public void An_exchange_that_finished_leaves_nothing_behind()
    {
        this.Running();
        File.WriteAllBytes(this.Previous, KeptBinary);

        Assert.Equal(SwapOutcome.Exchanged, this.swap.SwapWithPrevious(this.Binary));
        Assert.False(this.swap.Observe(this.Binary).HasInterruptedSwap);
        Assert.False(File.Exists(this.Binary + ".incoming"));
    }

    /// `Restore` undoes a swap that failed half way, so it acts only when the running binary
    /// has gone missing. Asserted because the two methods look alike and do opposite things.
    [Fact]
    public void Restoring_leaves_a_host_that_is_intact_alone()
    {
        this.Running();
        File.WriteAllBytes(this.Previous, KeptBinary);

        this.swap.Restore(this.Binary);

        Assert.Equal(OldBinary, File.ReadAllBytes(this.Binary));
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
