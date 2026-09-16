using Ripcord.Domain.Checks;
using Ripcord.Domain.Failover;

namespace Ripcord.Domain.Updates;

/// What replacing this host's binary does to the pair, whichever direction it is replaced in.
///
/// `update` and `rollback` say the same two things because it is the same fact: afterwards the
/// two hosts run different builds, and a sequence spanning both is refused while they do. Only
/// the verb differs, so only the verb is passed in — two copies of this drifted apart the day
/// one of them gained a sentence.
internal static class PairConsequences
{
    public static List<string> For(
        VersionSkew skew,
        OperatingMode mode,
        string peerHostName,
        string onceItChanges,
        string untilThePeer,
        string whatItDelays)
    {
        List<string> warnings =
        [
            skew.Verdict == VersionSkewVerdict.Different
                ? $"the two hosts already run different builds ({skew.Explanation}); "
                    + $"a failover spanning both is refused until {peerHostName} matches"
                : $"{onceItChanges}, a failover spanning both hosts is refused until "
                    + $"{peerHostName} {untilThePeer}",
        ];

        if (mode == OperatingMode.FailedOver)
        {
            warnings.Add(
                "this pair is failed over: production is running here, and the failback is "
                + $"what {whatItDelays} until {peerHostName} matches");
        }

        return warnings;
    }
}
