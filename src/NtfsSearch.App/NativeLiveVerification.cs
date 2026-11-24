using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal static class NativeLiveVerification
{
    public static async Task<int> RunAsync(string reportPath)
    {
        if (!AppProcess.HasAdministratorRights())
        {
            using var child = AppProcess.Start(["--verify-live", reportPath], elevate: true);
            await child.WaitForExitAsync(); return child.ExitCode;
        }
        string parent = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        string test = Path.Combine(parent, "Case-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(parent, "VerificationData");
        Directory.CreateDirectory(test); Directory.CreateDirectory(data);
        string canonical = NativeJournal.CanonicalPathForPath(test) ?? test;
        char drive = canonical[0];
        var references = new HashSet<ulong>();
        var stages = new List<object>();
        try
        {
            ulong directory = NativeJournal.ReferenceForPath(test); references.Add(directory);
            var initialState = NativeJournal.GetCurrentFileStateById(drive, directory);
            var builder = new IndexBuilder();
            foreach (var link in initialState.Links)
                builder.Add(link.ParentPath, link.Name, isDirectory: true, fileReference: directory, parentReference: link.ParentReference);
            var baseline = builder.Build(); SnapshotStore.Save(baseline, Path.Combine(data, "base.ntidx"));
            var initial = NativeJournal.Query(drive).Position;
            var live = new LiveVolumeIndex(baseline, initial);
            using var writer = new ChangeLogStore(data, "base.ntidx", initial, true);
            void Drain(LiveVolumeIndex view)
            {
                var end = NativeJournal.Query(drive).Position;
                var resolver = new JournalChangeResolver(data, reference => NativeJournal.GetCurrentFileStateById(drive, reference));
                while (writer.Checkpoint.Position.NextUsn < end.NextUsn)
                {
                    var batch = NativeJournal.Read(drive, writer.Checkpoint.Position, end.NextUsn, 2048);
                    if (batch.To == batch.From) throw new InvalidDataException("USN验证游标未推进。");
                    var selected = batch with { Records = batch.Records.Where(r => references.Contains(r.FileReference)).ToArray() };
                    var transaction = resolver.Resolve(selected, writer.Checkpoint.Position);
                    var committed = writer.Append(transaction);
                    foreach (var change in writer.ReadAfter(view.AppliedSequence)) view.Apply(change);
                    if (committed.Position.NextUsn > end.NextUsn) throw new InvalidDataException("USN验证越过终点。");
                }
            }
            SearchResult Search(LiveVolumeIndex view)
            { var catalog = new SearchCatalog(); catalog.PublishLayers("test", view.Layers); return catalog.Search(canonical); }

            string first = Path.Combine(test, "one.txt"), alias = Path.Combine(test, "alias.txt");
            File.WriteAllText(first, "live verification");
            if (!CreateHardLinkW(alias, first, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            ulong file = NativeJournal.ReferenceForPath(first); references.Add(file);
            var linked = NativeJournal.GetCurrentFileStateById(drive, file);
            if (linked.Links.Count != 2) throw new InvalidDataException("实际硬链接数量不符。");
            string folder = Path.Combine(test, "child"); Directory.CreateDirectory(folder);
            references.Add(NativeJournal.ReferenceForPath(folder));
            string child = Path.Combine(folder, "child.txt"); File.WriteAllText(child, "child");
            references.Add(NativeJournal.ReferenceForPath(child));
            Drain(live);
            if (Search(live).TotalCount != 5) throw new InvalidDataException("新增与硬链接实时数量错误。");
            stages.Add(new { Stage = "create_and_hardlink", Count = Search(live).TotalCount, HardLinks = linked.Links.Count });

            string renamed = Path.Combine(test, "renamed.txt"); File.Move(first, renamed); File.Delete(alias);
            string moved = Path.Combine(test, "moved"); Directory.Move(folder, moved);
            Drain(live); var afterMove = Search(live);
            if (afterMove.TotalCount != 4 || !afterMove.Rows.Any(r => r.FullPath.EndsWith(@"moved\child.txt", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("目录移动或硬链接删除更新失败。");
            stages.Add(new { Stage = "rename_move_unlink", Count = afterMove.TotalCount });
            string checkpoint = Path.Combine(data, "view.state"); live.SaveCheckpoint(checkpoint, live.AppliedSequence);

            string offline = Path.Combine(test, "offline.txt"); File.WriteAllText(offline, "offline");
            references.Add(NativeJournal.ReferenceForPath(offline)); File.Delete(renamed);
            File.Move(Path.Combine(moved, "child.txt"), Path.Combine(test, "final.txt"));
            live = LiveVolumeIndex.LoadCheckpoint(SnapshotStore.Load(Path.Combine(data, "base.ntidx")), checkpoint);
            Drain(live); var restored = Search(live);
            if (restored.TotalCount != 4 || !restored.Rows.Any(r => r.Name == "offline.txt") || restored.Rows.Any(r => r.Name == "renamed.txt"))
                throw new InvalidDataException("检查点恢复与停机变化回放失败。");
            stages.Add(new { Stage = "checkpoint_and_offline_replay", Count = restored.TotalCount });
            Directory.Delete(test, true); Drain(live);
            if (Search(live).TotalCount != 0) throw new InvalidDataException("递归删除后数量未归零。");
            stages.Add(new { Stage = "delete_tree", Count = 0 });
            AtomicJson.Write(reportPath, new { Success = true, drive, Stages = stages, FullVolumeRescans = 0 });
            return 0;
        }
        finally { if (Directory.Exists(test)) Directory.Delete(test, true); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, nint security);
}
