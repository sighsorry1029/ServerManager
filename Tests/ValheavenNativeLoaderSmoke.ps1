param([string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    & (Get-Command pwsh -ErrorAction Stop).Source -NoProfile -File $PSCommandPath -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Valheaven native-loader smoke test failed.' }
    exit 0
}

$root = Split-Path -Parent $PSScriptRoot
$source = Get-Content -LiteralPath (Join-Path $root 'AntiCheat/ExternalProcessDetector.cs') -Raw
# Compile the real detector with small dependency stubs. Test files contain only
# synthetic bytes and are never loaded or executed as DLLs. No cheat download.
$source = $source.Replace('namespace ServerManager;', 'namespace ServerManager {') + "`n}"
$harness = @'
namespace ServerManager
{
    internal enum DetectionEvidence
    {
        CheatEngineProcess, CheatEngineInjectedModule, ArtMoneyProcess,
        WeModProcess, SharpMonoInjectorProcess, GenericCheatProcess,
        GenericInjectProcess, GenericTrainerProcess, ValheavenNativeLoaderDetected = 202
    }
    internal static class IntegrityCanonical
    {
        internal static bool IsFatal(System.Exception error) =>
            error is System.OutOfMemoryException || error is System.StackOverflowException ||
            error is System.AccessViolationException || error is System.AppDomainUnloadedException;
    }
    public static class NativeLoaderSmoke
    {
        private static readonly System.Type Detector = typeof(ExternalProcessDetector);
        private const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        private static int assertions;
        private static System.Reflection.FieldInfo Field(string name) => Detector.GetField(name, Flags)!;
        private static object Budget() => System.Activator.CreateInstance(
            Detector.GetNestedType("NativeHashScanBudget", System.Reflection.BindingFlags.NonPublic)!, true)!;
        private static void SetBudget(object budget, string field, object value) =>
            budget.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(budget, value);
        private static int Files(object budget) => (int)budget.GetType().GetField(
            "Files", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(budget)!;
        private static void Assert(bool okay, string reason)
        {
            ++assertions;
            if (!okay) throw new System.InvalidOperationException(reason);
        }
        private static bool Hash(string path, object budget, out string hash)
            => Hash(path, budget, out hash, out _);
        private static bool Hash(string path, object budget, out string hash, out bool deferred)
        {
            object[] args = { path, budget, "", false };
            bool okay = (bool)Detector.GetMethod("TryGetNativeModuleHash", Flags)!.Invoke(null, args)!;
            hash = (string)args[2];
            deferred = (bool)args[3];
            return okay;
        }
        private static System.Collections.Generic.HashSet<DetectionEvidence> Scan(string[] paths, out bool available)
        {
            var evidence = new System.Collections.Generic.HashSet<DetectionEvidence>();
            available = (bool)Detector.GetMethod("TryScanNativeLoaderFiles", Flags)!.Invoke(null, new object[] { paths, evidence })!;
            return evidence;
        }
        private static string Digest(byte[] value)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return System.BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant();
        }
        private static string Make(string root, string folder, byte[] bytes, string name = "version.dll")
        {
            string directory = System.IO.Path.Combine(root, folder);
            System.IO.Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, name);
            System.IO.File.WriteAllBytes(path, bytes);
            return path;
        }
        public static int Run(string root)
        {
            var known = (System.Collections.Generic.HashSet<string>)Field("ValheavenNativeLoaderHashes").GetValue(null)!;
            Assert(known.Count == 3, "Keep exactly the three reviewed upstream signatures.");
            foreach (string signature in known)
                Assert(System.Text.RegularExpressions.Regex.IsMatch(signature, "^[0-9a-f]{64}$"), "Require full SHA-256 values.");

            var candidates = (System.Collections.Generic.HashSet<string>)Field("NativeLoaderCandidateNames").GetValue(null)!;
            Assert(candidates.Contains("VERSION.DLL"), "Candidate names must be case insensitive.");
            foreach (string rejected in new[] { "version.dll.bak", "myversion.dll", "version", "cheat.dll", "version.dll:stream", "version.dll " })
                Assert(!candidates.Contains(rejected), "No prefix/substring candidate: " + rejected);

            byte[] initial = { 1, 2, 3, 4 };
            string path = Make(root, "normal", initial);
            var first = Budget();
            Assert(Hash(path, first, out string hash) && hash == Digest(initial) && Files(first) == 1, "Hash complete file bytes.");
            Assert(Scan(new[] { path }, out bool available).Count == 0 && available, "Proxy DLL name alone is never evidence.");

            var cached = Budget();
            SetBudget(cached, "Files", 4);
            Assert(Hash(path, cached, out hash) && hash == Digest(initial) && Files(cached) == 4, "Unchanged metadata must bypass repeat file hashing.");
            string normalDirectory = System.IO.Path.GetDirectoryName(path)!;
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(normalDirectory, "child"));
            Assert(Hash(System.IO.Path.Combine(normalDirectory, "child", "..", "version.dll"), cached, out hash), "Normalize paths before cache lookup.");

            byte[] replacement = { 5, 6, 7, 8 };
            System.IO.File.WriteAllBytes(path, replacement);
            System.IO.File.SetLastWriteTimeUtc(path, System.DateTime.UtcNow.AddSeconds(3));
            Assert(!Hash(path, cached, out hash) && hash.Length == 0, "Changed metadata must not use stale cached hash.");
            var changed = Budget();
            Assert(Hash(path, changed, out hash) && hash == Digest(replacement) && Files(changed) == 1, "Rehash a changed loaded path.");

            string zero = Make(root, "zero", System.Array.Empty<byte>());
            Assert(!Hash(zero, Budget(), out hash) && hash.Length == 0, "Empty files are not valid hash evidence.");
            Assert(!Hash("version.dll", Budget(), out hash), "Reject relative file paths.");
            Assert(!Hash(Make(root, "noncandidate", initial, "ordinary.dll"), Budget(), out hash), "Do not hash arbitrary module names.");
            Assert(!Hash(System.IO.Path.Combine(root, "missing", "version.dll"), Budget(), out hash), "Missing file is diagnostic only.");

            string oversized = Make(root, "oversized", initial);
            using (var stream = new System.IO.FileStream(oversized, System.IO.FileMode.Open, System.IO.FileAccess.Write))
                stream.SetLength(8 * 1024 * 1024 + 1);
            var largeBudget = Budget();
            Assert(!Hash(oversized, largeBudget, out hash) && Files(largeBudget) == 0, "Oversized module must be skipped before reading bytes.");
            Assert(Scan(new[] { oversized }, out available).Count == 0 && !available, "Oversized module is not a cheat.");

            string fresh = Make(root, "budget", initial);
            var fileLimit = Budget();
            SetBudget(fileLimit, "Files", 4);
            Assert(!Hash(fresh, fileLimit, out hash, out bool deferred) && deferred && Files(fileLimit) == 4, "File-count budget is a scheduler deferral, not detector failure.");
            var byteLimit = Budget();
            SetBudget(byteLimit, "Bytes", (long)(16 * 1024 * 1024 - 1));
            Assert(!Hash(fresh, byteLimit, out hash, out deferred) && deferred && Files(byteLimit) == 0, "Byte-count budget is a scheduler deferral, not detector failure.");
            var expired = Budget();
            System.Threading.Thread.Sleep(225);
            Assert(!Hash(fresh, expired, out hash, out deferred) && deferred && Files(expired) == 0, "Cooperative elapsed budget must defer uncached files, not report detector failure.");
            var afterDeferral = Budget();
            Assert(Hash(fresh, afterDeferral, out hash) && Files(afterDeferral) == 1, "Skipped hashes must not become cache entries.");

            // Temporarily use the exact hash of harmless synthetic bytes so the
            // same production matching path has a deterministic positive test.
            string syntheticSignature = Digest(initial);
            Assert(known.Add(syntheticSignature), "Synthetic signature unexpectedly matched a production value.");
            try
            {
                string positive = Make(root, "positive", initial);
                var detected = Scan(new[] { positive }, out available);
                Assert(available && detected.SetEquals(new[] { DetectionEvidence.ValheavenNativeLoaderDetected }), "Exact full hash must produce only native-loader evidence.");
                System.IO.File.WriteAllBytes(positive, new byte[] { 1, 2, 3 });
                Assert(Scan(new[] { positive }, out available).Count == 0 && available, "Changed length invalidates positive cache.");

                var deferredPaths = new string[5];
                for (int i = 0; i < 4; ++i)
                    deferredPaths[i] = Make(root, "defer" + i, new byte[] { 10, (byte)i });
                deferredPaths[4] = Make(root, "defer-positive", initial);
                Field("nativeScanStart").SetValue(null, 0);
                Assert(Scan(deferredPaths, out available).Count == 0 && available, "An ordinary budget-limited scan must remain healthy.");
                var unconsumed = Budget();
                SetBudget(unconsumed, "Files", 4);
                Assert(!Hash(deferredPaths[4], unconsumed, out hash, out deferred) && deferred, "An uninspected deferred file must not be cached.");
                Assert(Scan(deferredPaths, out available).Contains(DetectionEvidence.ValheavenNativeLoaderDetected) && available, "A later scan must inspect deferred candidates without an unavailable warning.");

                var lockedPaths = new System.Collections.Generic.List<string>();
                var locks = new System.Collections.Generic.List<System.IO.FileStream>();
                try
                {
                    for (int i = 0; i < 4; ++i)
                    {
                        string locked = Make(root, "locked" + i, initial);
                        lockedPaths.Add(locked);
                        locks.Add(new System.IO.FileStream(locked, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None));
                    }
                    Assert(Scan(lockedPaths.ToArray(), out available).Count == 0 && !available, "Unreadable files cannot become evidence.");
                    lockedPaths.Add(Make(root, "after-locked", initial));
                    Field("nativeScanStart").SetValue(null, 0);
                    Assert(Scan(lockedPaths.ToArray(), out available).Count == 0 && !available, "Respect the first scan's four failed attempts.");
                    Assert(Scan(lockedPaths.ToArray(), out available).Contains(DetectionEvidence.ValheavenNativeLoaderDetected), "Rotate so failed reads do not starve later loaded candidates.");
                }
                finally { foreach (var stream in locks) stream.Dispose(); }
            }
            finally { known.Remove(syntheticSignature); }

            for (int i = 0; i < 132; ++i)
                Assert(Hash(Make(root, "cache" + i, new[] { (byte)i }), Budget(), out hash), "Hash cache fixture.");
            var cache = (System.Collections.IDictionary)Field("NativeHashCache").GetValue(null)!;
            var order = (System.Collections.ICollection)Field("NativeHashCacheOrder").GetValue(null)!;
            Assert(cache.Count == 128 && order.Count == 128, "Bound cache and eviction bookkeeping.");
            Assert(!cache.Contains(path), "Evict oldest path without unbounded retention.");
            Assert(known.Count == 3, "Tests must restore the production signature catalog.");
            return assertions;
        }
    }
}
'@

Add-Type -TypeDefinition ("#nullable enable`n" + $source + "`n" + $harness) -Language CSharp
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$testDirectory = Join-Path $temporaryRoot ('ServerManager-NativeLoader-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $count = [ServerManager.NativeLoaderSmoke]::Run($testDirectory)
    Write-Output "PASS: $count source-linked native-loader assertions; only synthetic byte files used."
}
finally {
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notmatch '^ServerManager-NativeLoader-[0-9a-f]{32}$') {
        throw 'Refusing to remove an unexpected native-loader test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
