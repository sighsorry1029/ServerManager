using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// The two methods under test are inserted verbatim from the runtime source.
// Only publication and immutable metadata are replaced by in-memory boundaries.
public static class CharacterCheckpointCompletionProbe
{
    private enum ServerManagerCharacterCommitScope { AllRetainedShadowsAtCutoff, PartialRetainedShadowsAtCutoff }
    private sealed class Snapshot { internal long Revision; }
    private sealed class Entry { internal string StorageKey; internal Snapshot Snapshot; }
    private sealed class PendingCharacterCheckpointEntry { internal object Service; internal Entry Entry; }
    private sealed class CharacterCheckpointTarget { internal long Revision; internal bool AttemptFinished, Persisted; }
    private sealed class CharacterCheckpointProgress
    {
        internal object Service;
        internal string OperationId, Warning = "";
        internal Guid CheckpointId = Guid.NewGuid();
        internal Dictionary<string, CharacterCheckpointTarget> Targets = new Dictionary<string, CharacterCheckpointTarget>();
    }
    private sealed class Result { internal string Operation; internal int Captured, Persisted, Pending; internal ServerManagerCharacterCommitScope Scope; }
    private static readonly List<CharacterCheckpointProgress> CharacterCheckpointProgresses = new List<CharacterCheckpointProgress>();
    private static readonly List<Result> Results = new List<Result>();

    // SOURCE_LINKED_CHECKPOINT_COMPLETION

    private static void PublishWorldCharacterCheckpointResult(string operation, Guid id, int captured,
        ServerManagerCharacterCommitScope scope, int persisted, int pending, string warning)
    {
        Results.Add(new Result { Operation = operation, Captured = captured, Scope = scope, Persisted = persisted, Pending = pending });
    }
    public static void Verify()
    {
        object service = new object();
        Add(service, "mixed", 10, 10);
        Outcome(service, "a", 10, false);
        Require(Results.Count == 0, "A failed first player prematurely published while another player's first attempt was unobserved.");
        Outcome(new object(), "b", 10, true);
        Require(Results.Count == 0, "A stale service completion settled the new world's checkpoint.");
        Outcome(service, "b", 10, true);
        Require(Results.Count == 1 && Results[0].Captured == 2 && Results[0].Persisted == 1 && Results[0].Pending == 1 &&
            Results[0].Scope == ServerManagerCharacterCommitScope.PartialRetainedShadowsAtCutoff,
            "A failed disk attempt was reported as complete durability.");

        Add(service, "older", 11, 12);
        Add(service, "newer", 13, 14);
        Outcome(service, "a", 11, true);
        Require(Results.Count == 1, "Queued/as-yet unattempted storage was presented as completed.");
        Outcome(service, "b", 12, true);
        Require(Results.Count == 2 && Results[1].Operation == "older" && Results[1].Pending == 0,
            "The completed older cutoff did not settle independently.");
        Outcome(service, "a", 20, true);
        Require(Results.Count == 2, "A newer revision of one character settled an unrelated outstanding character.");
        Outcome(service, "b", 20, true);
        Require(Results.Count == 3 && Results[2].Operation == "newer" && Results[2].Persisted == 2 &&
            Results[2].Scope == ServerManagerCharacterCommitScope.AllRetainedShadowsAtCutoff,
            "Later successful coalesced revisions failed to cover earlier cutoff targets.");

        CharacterCheckpointProgress abandoned = Add(service, "shutdown", 21, 21);
        PublishCharacterCheckpointProgress(abandoned, true);
        Require(Results.Count == 4 && Results[3].Persisted == 0 && Results[3].Pending == 2,
            "Shutdown timeout fabricated completion for unfinished disk writes.");
        Require(CharacterCheckpointProgresses.Count == 0, "Finished progress metadata was retained.");
        Results.Clear();
    }
    private static CharacterCheckpointProgress Add(object service, string operation, long a, long b)
    {
        CharacterCheckpointProgress progress = new CharacterCheckpointProgress { Service = service, OperationId = operation };
        progress.Targets.Add("a", new CharacterCheckpointTarget { Revision = a });
        progress.Targets.Add("b", new CharacterCheckpointTarget { Revision = b });
        CharacterCheckpointProgresses.Add(progress);
        return progress;
    }
    private static void Outcome(object service, string key, long revision, bool persisted)
    {
        RecordCharacterCheckpointOutcome(new PendingCharacterCheckpointEntry { Service = service,
            Entry = new Entry { StorageKey = key, Snapshot = new Snapshot { Revision = revision } } }, persisted);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
