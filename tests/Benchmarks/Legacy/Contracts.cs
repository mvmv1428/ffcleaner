namespace FFCleaner.Benchmarks.Legacy;

public record ScanRoot(string Id, string Name, string Path, string Risk, bool CanSelect = true);
public record ScanProgress(int Enumerated, string CurrentPath);
public record FileRecord(string Id, string RuleId, string FullPath, long LogicalBytes,
    long? AllocatedBytes, string Identity, uint LinkCount, DateTime LastWriteUtc,
    DateTime LastAccessUtc, bool Eligible, string Reason);
public record ScanIssue(string RootId, string Path, string Code, string Message);
public record ScanResult(Guid ScanId, DateTime CreatedUtc, List<FileRecord> Files,
    List<ScanIssue> Issues, bool Cancelled, bool Limited, string RulesVersion = "1");
public record ManifestItem(string FileId, string Path, string Identity, long Length,
    DateTime LastWriteUtc);
public record PreviewManifest(Guid ScanId, DateTime CreatedUtc, DateTime ExpiresUtc,
    string RulesVersion, List<ManifestItem> Items, long LogicalBytes,
    long ConservativeBytes, string Warning, string Digest);
public record UserSettings(List<string> SelectedRootIds, List<string> Exclusions);
