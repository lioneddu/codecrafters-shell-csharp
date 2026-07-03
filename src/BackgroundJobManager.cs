using System.Diagnostics;

// Possible states of a tracked job.
enum JobStatus
{
	Running,
	Done
}

// Represents a single background process tracked by the shell.
class Job
{
	public int Id { get; }
	public Process Process { get; }
	public string CommandLine { get; }
	public JobStatus Status { get; set; } = JobStatus.Running;

	public Job(int id, Process process, string commandLine)
	{
		Id = id;
		Process = process;
		CommandLine = commandLine;
	}
}

// Keeps track of all background jobs started with '&'.
class BackgroundJobManager
{
	private static readonly List<Job> _jobs = new();

	// Register a newly started background process and return its assigned job number.
	public static int AddJob(Process process, string commandLine)
	{
		int jobId = _jobs.Count > 0 ? _jobs.Max(j => j.Id) + 1 : 1;
		_jobs.Add(new Job(jobId, process, commandLine));
		return jobId;
	}

	// Return a snapshot of all currently tracked jobs, in job-number order.
	public static List<Job> GetJobs()
	{
		return _jobs.OrderBy(j => j.Id).ToList();
	}

	// Remove a job from the tracked list, e.g. once it has finished and been reported.
	public static void RemoveJob(int jobId)
	{
		_jobs.RemoveAll(j => j.Id == jobId);
	}

	// Find a job by its id, or null if it isn't tracked.
	public static Job? GetJob(int jobId)
	{
		return _jobs.FirstOrDefault(j => j.Id == jobId);
	}

	/// <summary>
	/// Refresh job statuses, print every currently tracked job in bash's
	/// "jobs" format, then reap (remove) any job that has just finished.
	/// </summary>
	public static void PrintJobs()
	{
		// Detect jobs whose process has exited since the last check, without
		// blocking: WaitForExit() would wait, HasExited only polls the state.
		foreach (var job in _jobs.Where(j => j.Status == JobStatus.Running)) {
			if (job.Process.HasExited) {
				job.Status = JobStatus.Done;
			}
		}

		var jobList = GetJobsWithIndicators();

		if (jobList.Any()) {
			foreach (var (job, indicator) in jobList) {
				Console.WriteLine($"[{job.Id}]{indicator}  {job.Status.ToString().PadRight(24)} {job.CommandLine}");
			}
		}

		// Reap: remove jobs that were just reported as Done, so they don't
		// appear again on the next "jobs" call.
		var doneIds = _jobs.Where(j => j.Status == JobStatus.Done)
							.Select(j => j.Id)
							.ToList();

		foreach (var id in doneIds) {
			RemoveJob(id);
		}
	}

	// Returns the currently tracked jobs paired with their bash-style indicator:
	// '+' for the most recently started job (the "current" job),
	// '-' for the second most recently started job (the "previous" job),
	// ' ' for all others. Order follows job number, matching bash's "jobs" output.
	public static List<(Job Job, char Indicator)> GetJobsWithIndicators()
	{
		var ordered = GetJobs(); // already sorted by Id ascending

		var result = new List<(Job, char)>();

		for (int i = 0; i < ordered.Count; i++) {
			char indicator = ' ';

			if (i == ordered.Count - 1) {
				indicator = '+';
			}
			else if (i == ordered.Count - 2) {
				indicator = '-';
			}

			result.Add((ordered[i], indicator));
		}

		return result;
	}

	// Check every still-Running job to see if its process has exited, and flip
	// its status to Done accordingly. Returns the jobs that transitioned to
	// Done during this call, so the caller can print a one-time notification.
	public static List<Job> RefreshStatuses()
	{
		var newlyFinished = new List<Job>();

		foreach (var job in _jobs.Where(j => j.Status == JobStatus.Running)) {
			if (job.Process.HasExited) {
				job.Status = JobStatus.Done;
				newlyFinished.Add(job);
			}
		}

		return newlyFinished;
	}
}