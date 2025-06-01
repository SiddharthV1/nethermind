namespace Nethermind.StatsAnalyzer.Plugin.Tracer;

public interface IStatsAnalyzerTxTracer<TTrace>
{
    TTrace BuildResult(long fromBlock = 0, DateTime fromTimeStampDate = default, long toBlock = 0, DateTime toTimeStampDate = default);
}
