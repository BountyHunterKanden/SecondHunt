namespace MphRecomp.Campaign;

// (thread B keyframes, tests only: -replaypreptest walkdiff=) a match-state static the sim never reads, so a test can give
// one copy of a match something the other lacks, as an app-only hook or a warm process does, and prove the prepared
// keyframes still load exactly, matched by name, or are refused naming what's missing (instant seek always)
public static class ReplayWalkProbe
{
    public static object? Value;
}
