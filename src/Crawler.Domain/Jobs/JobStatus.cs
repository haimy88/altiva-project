namespace Crawler.Domain.Jobs;

public enum JobStatus { Pending, Running, Completed, Failed, Canceled }

public static class JobStatusExtensions
{
    /// <summary>Completed, Failed and Canceled are final: nothing changes a job after that.</summary>
    public static bool IsFinal(this JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled;
}
