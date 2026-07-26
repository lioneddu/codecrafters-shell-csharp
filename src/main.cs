using System.Diagnostics;
using System.Security;
using System.Text;

class Program
{
	// Path used to restore the terminal to its original settings on exit.
	private static string _savedTtySettings = string.Empty;

	// Returns the set of builtin commands that can meaningfully appear inside
	// a pipeline (i.e. ones that produce output rather than only side effects).
	private static readonly HashSet<string> _pipelineCapableBuiltins = new() { "echo", "type", "pwd", "jobs" };

	static void Main()
	{
		_EnableRawTerminalMode();

		try {

			string[] builtInCommands = { "echo", "exit", "type", "pwd", "cd", "complete", "jobs", "history" };

			Dictionary<string, string> customCompletions = new();

			var allCommands = new string[builtInCommands.Length];
			builtInCommands.CopyTo(allCommands, 0);

			string[] pathDirectories = Environment.GetEnvironmentVariable("PATH").Split(':');

			foreach (string pathDirectory in pathDirectories) {
				if (!Directory.Exists(pathDirectory))
					continue;

				foreach (string file in Directory.GetFiles(pathDirectory)) {
					allCommands = allCommands.Append(Path.GetFileName(file)).Distinct().ToArray();
				}
			}

			while (true) {
				// Report any background job that finished since the last prompt,
				// matching bash's "[n]+  Done   command" notification, then drop it
				// from tracking since it has already been reported.
				foreach (var finishedJob in BackgroundJobManager.RefreshStatuses()) {
					Console.WriteLine($"[{finishedJob.Id}]+  Done                    {finishedJob.CommandLine}");
					BackgroundJobManager.RemoveJob(finishedJob.Id);
				}

				Console.Write("$ ");

				string command = _ReadCommandLine(allCommands, customCompletions);

				// Detect a trailing '&' marking this command to run in the background.
				// The shell must not wait for it to finish before showing the next prompt.
				bool isBackground = false;
				string trimmedCommand = command.TrimEnd();

				if (trimmedCommand.EndsWith("&")) {
					isBackground = true;
					command = trimmedCommand[..^1].TrimEnd();
				}

				if (_ContainsUnquotedPipe(command)) {
					_ExecutePipeline(command, isBackground, builtInCommands);
					continue;
				}

				if (command.StartsWith("echo")) {
					string echoArguments = command.Substring(5);
					var arguments = _ParseArguments(echoArguments);

					RedirectMode echoStdoutMode = RedirectMode.None;
					RedirectMode echoStderrMode = RedirectMode.None;

					string stdoutFile = string.Empty;
					string stderrFile = string.Empty;

					var echoArgs = new List<string>();

					for (int i = 0; i < arguments.Count; i++) {
						string arg = arguments[i];

						if (arg == ">" || arg == "1>") {
							echoStdoutMode = RedirectMode.Overwrite;
							stdoutFile = arguments[++i];
							continue;
						}

						if (arg == ">>" || arg == "1>>") {
							echoStdoutMode = RedirectMode.Append;
							stdoutFile = arguments[++i];
							continue;
						}

						if (arg == "2>") {
							echoStderrMode = RedirectMode.Overwrite;
							stderrFile = arguments[++i];
							continue;
						}

						if (arg == "2>>") {
							echoStderrMode = RedirectMode.Append;
							stderrFile = arguments[++i];
							continue;
						}

						echoArgs.Add(arg);
					}

					string echoOutput = string.Join(" ", echoArgs) + "\n";

					if (echoStdoutMode == RedirectMode.None) {
						Console.Write(echoOutput);
					}
					else {
						_WriteOutput(echoOutput, echoStdoutMode, stdoutFile);
					}

					if (echoStderrMode != RedirectMode.None) {
						_WriteOutput(string.Empty, echoStderrMode, stderrFile);
					}

					continue;
				}

				if (command.StartsWith("type")) {

					string specifiedCommand = command.Split(" ")[1];

					if (builtInCommands.Contains(specifiedCommand)) {
						Console.WriteLine($"{specifiedCommand} is a shell builtin");
					}
					else {
						string path = _FindCommandInPath(specifiedCommand);

						if (string.IsNullOrEmpty(path))
							Console.WriteLine($"{specifiedCommand}: not found");
						else
							Console.WriteLine($"{specifiedCommand} is {path}");
					}

					continue;
				}

				if (command.StartsWith("pwd")) {
					Console.WriteLine(Directory.GetCurrentDirectory());
					continue;
				}

				if (command.StartsWith("cd")) {
					string[] cdArgs = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
					if (cdArgs.Length > 1) {
						if (cdArgs[1] == "~") {
							Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
						}
						else if (Directory.Exists(cdArgs[1])) {
							Directory.SetCurrentDirectory(cdArgs[1]);
						}
						else {
							Console.WriteLine($"cd: {cdArgs[1]}: No such file or directory");
						}
					}

					continue;
				}

				if (command.StartsWith("complete")) {
					string[] completeArgs = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
					if (completeArgs.Length > 1) {
						if (completeArgs[1] == "-p") {
							if (completeArgs.Length > 2) {
								string cmd = completeArgs[2];

								if (customCompletions.TryGetValue(cmd, out var script)) {
									Console.WriteLine($"complete -C '{script}' {cmd}");
								}
								else {
									Console.WriteLine($"complete: {cmd}: no completion specification");
								}
							}
						}

						if (completeArgs[1].ToLower() == "-c") {
							if (completeArgs.Length > 2) {
								customCompletions[completeArgs[3]] = $"{completeArgs[2]}";
							}
						}

						if (completeArgs[1].ToLower() == "-r") {
							if (completeArgs.Length > 2) {
								customCompletions.Remove(completeArgs[2]);
							}
						}
					}
					continue;
				}

				if (command.Equals("jobs")) {
					// List all background jobs
					BackgroundJobManager.PrintJobs();
					continue;
				}

				if (command.Equals("exit")) {
					break;

				}

				// Real command management.
				var tokens = _ParseArguments(command);
				string commandName = tokens[0];

				if (string.IsNullOrEmpty(_FindCommandInPath(commandName))) {
					Console.WriteLine($"{command}: command not found");
					continue;
				}

				var psi = _BuildProcessStartInfo(
					tokens,
					out RedirectMode stdoutMode, out string outputFilename,
					out RedirectMode stderrMode, out string errorFilename);

				if (isBackground) {
					// Background job: no redirection, no waiting. Output goes straight to
					// the terminal and the shell returns to the prompt immediately.
					psi.RedirectStandardOutput = false;
					psi.RedirectStandardError = false;

					var bgProcess = Process.Start(psi);
					int jobId = BackgroundJobManager.AddJob(bgProcess, command);

					Console.WriteLine($"[{jobId}] {bgProcess.Id}");
					continue;
				}

				// Foreground: existing blocking behavior.
				psi.RedirectStandardOutput = true;
				psi.RedirectStandardError = true;

				using var process = Process.Start(psi);

				string output = process.StandardOutput.ReadToEnd();
				string error = process.StandardError.ReadToEnd();
				process.WaitForExit();

				_WriteOutput(output, stdoutMode, outputFilename);

				if (stderrMode == RedirectMode.None) {
					Console.Error.Write(error);
				}
				else {
					_WriteOutput(error, stderrMode, errorFilename);
				}

			}

		}
		finally {
			_RestoreTerminalMode();
		}
	}

	// Disable canonical mode and local echo on the controlling terminal.
	// We read input one keystroke at a time (Console.ReadKey) and echo it
	// ourselves in _ReadCommandLine, so kernel-level echo must be turned off
	// to avoid every typed line being displayed twice.
	private static void _EnableRawTerminalMode()
	{
		_savedTtySettings = _RunSttyCommand("-g");
		_RunSttyCommand("-echo -icanon min 1 time 0");
	}

	// Restore the terminal settings that were active before we changed them.
	private static void _RestoreTerminalMode()
	{
		if (!string.IsNullOrEmpty(_savedTtySettings)) {
			_RunSttyCommand(_savedTtySettings);
		}
	}

	// Returns true if the command line contains a '|' outside of any quotes.
	private static bool _ContainsUnquotedPipe(string command)
	{
		bool inSingleQuotes = false;
		bool inDoubleQuotes = false;

		for (int i = 0; i < command.Length; i++) {
			char c = command[i];

			if (!inDoubleQuotes && c == '\'') inSingleQuotes = !inSingleQuotes;
			else if (!inSingleQuotes && c == '"') inDoubleQuotes = !inDoubleQuotes;
			else if (!inSingleQuotes && !inDoubleQuotes && c == '|') return true;
		}

		return false;
	}

	// Splits a command line into pipeline stages on unquoted '|' characters.
	// Each returned segment is the raw, untrimmed text of one stage.
	private static List<string> _SplitPipelineStages(string command)
	{
		var stages = new List<string>();
		var current = new StringBuilder();

		bool inSingleQuotes = false;
		bool inDoubleQuotes = false;

		for (int i = 0; i < command.Length; i++) {
			char c = command[i];

			if (!inDoubleQuotes && c == '\'') inSingleQuotes = !inSingleQuotes;
			else if (!inSingleQuotes && c == '"') inDoubleQuotes = !inDoubleQuotes;

			if (!inSingleQuotes && !inDoubleQuotes && c == '|') {
				stages.Add(current.ToString().Trim());
				current.Clear();
				continue;
			}

			current.Append(c);
		}

		stages.Add(current.ToString().Trim());
		return stages;
	}

	// Run an stty command against the current controlling terminal and
	// return its trimmed stdout (used both to query and to apply settings).
	private static string _RunSttyCommand(string arguments)
	{
		var psi = new ProcessStartInfo {
			FileName = "stty",
			Arguments = arguments,
			UseShellExecute = false,
			RedirectStandardOutput = true
		};

		using var process = Process.Start(psi);
		string output = process.StandardOutput.ReadToEnd().Trim();
		process.WaitForExit();

		return output;
	}

	private static string _ReadCommandLine(string[] allCommands, Dictionary<string, string> customCompletions)
	{
		var buffer = new StringBuilder();
		bool previousWasTab = false;

		while (true) {
			var key = Console.ReadKey(true);

			if (key.Key == ConsoleKey.Enter) {
				Console.WriteLine();
				return buffer.ToString();
			}

			if (key.Key == ConsoleKey.Backspace) {
				if (buffer.Length > 0) {
					buffer.Length--;
					Console.Write("\b \b");
				}

				previousWasTab = false;
				continue;
			}

			if (key.Key == ConsoleKey.Tab) {
				string line = buffer.ToString();
				int lastSpace = line.LastIndexOf(' ');

				List<string> matches;
				string prefix;
				string searchDirectory;

				if (customCompletions.TryGetValue(line.Trim(), out var completionDir)) {
					searchDirectory = completionDir;
				}
				else {
					searchDirectory = ".";  // default
				}

				if (lastSpace == -1) {
					// Command completion
					prefix = line;

					matches = allCommands
						.Where(c => c.StartsWith(prefix))
						.OrderBy(c => c)
						.ToList();
				}
				else {

					// Custom completion.
					// The command name is always the first word on the line, regardless of
					// how many words have been typed after it.
					var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
					string commandName = words.Length > 0 ? words[0] : string.Empty;

					if (customCompletions.TryGetValue(commandName, out var script)) {
						// The word currently being completed is whatever follows the last space.
						// It is empty when the line ends with a space (i.e. starting a new word).
						string wordBeingCompleted = line[(lastSpace + 1)..];

						// The previous word is the word immediately before the one being completed.
						// If there isn't one, pass an empty string.
						string previousWord = string.Empty;
						bool endsWithSpace = line.Length > 0 && line[^1] == ' ';
						int previousWordIndex = endsWithSpace ? words.Length - 1 : words.Length - 2;

						if (previousWordIndex >= 0 && previousWordIndex < words.Length) {
							previousWord = words[previousWordIndex];
						}

						var psi = new ProcessStartInfo {
							FileName = script,
							RedirectStandardOutput = true,
							UseShellExecute = false
						};

						// Pass argv[1] = command, argv[2] = word being completed, argv[3] = previous word.
						psi.ArgumentList.Add(commandName);
						psi.ArgumentList.Add(wordBeingCompleted);
						psi.ArgumentList.Add(previousWord);

						//Save env variables
						Environment.SetEnvironmentVariable("COMP_LINE", line);
						Environment.SetEnvironmentVariable("COMP_POINT", line.Length.ToString());

						using var process = Process.Start(psi);

						string rawOutput = process.StandardOutput.ReadToEnd();

						process.WaitForExit();

						// The completer prints one candidate per line; split on both \n and \r\n
						// and drop empty entries (e.g. a trailing blank line).
						string[] candidates = rawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
							.Select(c => c.TrimEnd('\r'))
							.Where(c => !string.IsNullOrEmpty(c))
							.ToArray();

						// If the completer printed nothing, there are no candidates:
						// ring the bell and leave the input line untouched.
						if (candidates.Length == 0) {
							Console.Write("\x07");
							previousWasTab = false;
							continue;
						}

						// Candidates must be sorted alphabetically before being handed to Complete().
						matches = candidates.OrderBy(c => c, StringComparer.Ordinal).ToList();
						prefix = wordBeingCompleted;

						Complete(matches, prefix, ".", buffer, ref previousWasTab);
						continue;
					}

					// Filename completion
					string token = line[(lastSpace + 1)..];

					string filePrefix;
					string pathPrefix;

					if (token.Contains('/')) {
						int lastSlash = token.LastIndexOf('/');

						pathPrefix = token[..(lastSlash + 1)];
						filePrefix = token[(lastSlash + 1)..];

						searchDirectory = string.IsNullOrEmpty(pathPrefix)
							? "."
							: pathPrefix;
					}
					else {
						pathPrefix = "";
						filePrefix = token;
						searchDirectory = ".";
					}

					if (!Directory.Exists(searchDirectory)) {
						Console.Write("\x07");
						previousWasTab = false;
						continue;
					}

					matches = Directory
						.EnumerateFileSystemEntries(searchDirectory)
						.Where(e => Path.GetFileName(e).StartsWith(filePrefix))
						.OrderBy(Path.GetFileName)
						.Select(e => pathPrefix + Path.GetFileName(e))
						.ToList();

					prefix = token;
				}

				Complete(matches, prefix, searchDirectory, buffer, ref previousWasTab);
				continue;
			}

			previousWasTab = false;
			buffer.Append(key.KeyChar);
			Console.Write(key.KeyChar);
		}
	}

	private static void Complete(List<string> matches,
									string prefix,
									string searchDirectory,
									StringBuilder buffer,
									ref bool previousWasTab)
	{
		if (matches.Count == 0) {
			Console.Write("\x07");
			previousWasTab = false;
			return;
		}

		if (matches.Count == 1) {
			string completion = matches[0];
			string missing = completion.Substring(prefix.Length);

			buffer.Append(missing);
			Console.Write(missing);

			if (Directory.Exists(completion)) {
				buffer.Append('/');
				Console.Write('/');
			}
			else {
				buffer.Append(' ');
				Console.Write(' ');
			}

			previousWasTab = false;
			return;
		}

		string lcp = LongestCommonPrefix(matches);

		if (lcp.Length > prefix.Length) {
			string missing = lcp.Substring(prefix.Length);

			buffer.Append(missing);
			Console.Write(missing);

			previousWasTab = false;
			return;
		}

		if (!previousWasTab) {
			Console.Write("\x07");
			previousWasTab = true;
			return;
		}

		Console.WriteLine();
		var displayMatches = matches
			.Select(m => {
				if (Directory.Exists(m))
					return m + "/";

				return m;
			});

		Console.WriteLine(string.Join("  ", displayMatches));

		Console.Write("$ ");
		Console.Write(buffer.ToString());

		previousWasTab = false;
	}

	private static string LongestCommonPrefix(List<string> strings)
	{
		if (strings.Count == 0)
			return string.Empty;

		string prefix = strings[0];

		foreach (var s in strings.Skip(1)) {
			int i = 0;

			while (i < prefix.Length &&
				   i < s.Length &&
				   prefix[i] == s[i]) {
				i++;
			}

			prefix = prefix.Substring(0, i);

			if (prefix.Length == 0)
				break;
		}

		return prefix;
	}

	enum RedirectMode
	{
		None,
		Overwrite,
		Append
	}

	private static void _WriteOutput(string output, RedirectMode mode, string fileName)
	{
		switch (mode) {
			case RedirectMode.None:
				Console.Write(output);
				break;

			case RedirectMode.Overwrite:
				File.WriteAllText(fileName, output);
				break;

			case RedirectMode.Append:
				File.AppendAllText(fileName, output);
				break;
		}
	}

	private static List<string> _ParseArguments(string input)
	{
		var args = new List<string>();
		var current = new StringBuilder();

		bool inSingleQuotes = false;
		bool inDoubleQuotes = false;

		for (int i = 0; i < input.Length; i++) {
			char c = input[i];

			// Backslash escaping handling
			if (!inSingleQuotes &&
				!inDoubleQuotes &&
				c == '\\') {
				if (i + 1 < input.Length) {
					current.Append(input[++i]);
				}

				continue;
			}

			if (inDoubleQuotes && c == '\\') {
				if (i + 1 < input.Length) {
					char next = input[i + 1];

					if (next == '"' ||
						next == '\\' ||
						next == '$' ||
						next == '`') {
						current.Append(next);
						i++;
						continue;
					}
				}

				current.Append('\\');
				continue;
			}

			if (!inDoubleQuotes && c == '\'') {
				inSingleQuotes = !inSingleQuotes;
				continue;
			}

			if (!inSingleQuotes && c == '"') {
				inDoubleQuotes = !inDoubleQuotes;
				continue;
			}

			if (!inSingleQuotes &&
				!inDoubleQuotes &&
				char.IsWhiteSpace(c)) {
				if (current.Length > 0) {
					args.Add(current.ToString());
					current.Clear();
				}

				continue;
			}

			current.Append(c);
		}

		if (current.Length > 0) {
			args.Add(current.ToString());
		}

		return args;
	}

	private static string _FindCommandInPath(string specifiedCommand)
	{
		try {

			string path = Environment.GetEnvironmentVariable("PATH");
			bool isFound = false;
			if (!string.IsNullOrEmpty(path)) {

				foreach (string dir in path.Split(':')) {
					string fullPath = Path.Combine(dir, specifiedCommand);
					if (File.Exists(fullPath) && File.GetUnixFileMode(fullPath).HasFlag(UnixFileMode.UserExecute)) {
						isFound = true;
						return fullPath;
					}
				}

				if (!isFound) {
					return string.Empty;
				}

			}

			return string.Empty;
		}
		catch (SecurityException exception) {
			return string.Empty;
		}
	}

	/// <summary>
	/// Executes a pipeline of commands connected by '|', where each stage can be
	/// either an external process or a shell builtin. Standard output of each
	/// stage feeds standard input of the next. If isBackground is true, the
	/// whole pipeline is registered as a single job and the shell returns to
	/// the prompt immediately without waiting for the last stage.
	/// </summary>
	private static void _ExecutePipeline(string command, bool isBackground, string[] builtInCommands)
	{
		var stageTexts = _SplitPipelineStages(command);
		int stageCount = stageTexts.Count;

		var processes = new Process?[stageCount];
		var stdoutModes = new RedirectMode[stageCount];
		var isBuiltin = new bool[stageCount];
		var builtinOutputs = new string[stageCount];

		RedirectMode lastStdoutMode = RedirectMode.None;
		string lastStdoutFile = string.Empty;

		for (int i = 0; i < stageCount; i++) {
			var tokens = _ParseArguments(stageTexts[i]);

			if (tokens.Count == 0) {
				Console.WriteLine("syntax error: empty pipeline stage");
				return;
			}

			string commandName = tokens[0];
			bool isFirst = i == 0;
			bool isLast = i == stageCount - 1;

			if (_pipelineCapableBuiltins.Contains(commandName)) {
				// Builtins run in-process: no Process object, output computed
				// eagerly as a string and wired to the next stage below.
				isBuiltin[i] = true;
				builtinOutputs[i] = _ExecuteBuiltinForPipeline(stageTexts[i], builtInCommands);

				if (isLast) {
					lastStdoutMode = RedirectMode.None;
				}

				continue;
			}

			if (string.IsNullOrEmpty(_FindCommandInPath(commandName))) {
				Console.WriteLine($"{commandName}: command not found");
				return;
			}

			var psi = _BuildProcessStartInfo(
				tokens,
				out RedirectMode stdoutMode, out string stdoutFile,
				out RedirectMode stderrMode, out string stderrFile);

			stdoutModes[i] = stdoutMode;

			if (isLast) {
				lastStdoutMode = stdoutMode;
				lastStdoutFile = stdoutFile;
			}

			// A builtin never reads stdin, so there is no point handing an
			// external process a pipe to a stage that will just ignore it —
			// still redirect it, though, since we need to drain and discard it
			// ourselves rather than let it print straight to the console.
			psi.RedirectStandardInput = !isFirst;
			psi.RedirectStandardOutput = !isLast || stdoutMode != RedirectMode.None;
			psi.RedirectStandardError = stderrMode != RedirectMode.None;

			processes[i] = Process.Start(psi);
		}

		// Wire each stage's output to the next stage's input, streaming as data
		// arrives. Tasks are intentionally not awaited as a group: an earlier
		// stage (e.g. "tail -f") may keep running after the pipeline's visible
		// output is complete, and waiting for it here would hang the shell.
		for (int i = 0; i < stageCount - 1; i++) {
			int index = i;
			bool nextIsBuiltin = isBuiltin[index + 1];

			if (isBuiltin[index]) {
				// Builtin stage feeding the next stage: its output is already a
				// complete string (builtins are not streaming). If the next
				// stage is external, write it once to that process's stdin.
				// If the next stage is also a builtin, there is nothing to feed
				// it (builtins don't read stdin), so there is nothing to do.
				if (!nextIsBuiltin) {
					var nextProcess = processes[index + 1]!;

					Task.Run(() => {
						try {
							nextProcess.StandardInput.Write(builtinOutputs[index]);
							nextProcess.StandardInput.Close();
						}
						catch (IOException) { /* downstream stage exited early */ }
						catch (ObjectDisposedException) { /* same as above */ }
					});
				}

				continue;
			}

			if (stdoutModes[index] != RedirectMode.None)
				continue; // this stage's stdout was explicitly redirected to a file

			var sourceProcess = processes[index]!;

			Task.Run(() => {
				try {
					var source = sourceProcess.StandardOutput.BaseStream;
					byte[] buffer = new byte[4096];
					int read;

					if (nextIsBuiltin) {
						// The next stage ignores stdin entirely: drain this
						// stage's output and discard it, so the producer isn't
						// blocked writing to a pipe nobody reads (this matches
						// bash: "ls | type exit" does not print ls's output).
						while ((read = source.Read(buffer, 0, buffer.Length)) > 0) {
							// intentionally discarded
						}
						return;
					}

					var dest = processes[index + 1]!.StandardInput.BaseStream;

					while ((read = source.Read(buffer, 0, buffer.Length)) > 0) {
						dest.Write(buffer, 0, read);
						dest.Flush();
					}

					processes[index + 1]!.StandardInput.Close();
				}
				catch (IOException) { /* downstream stage exited early */ }
				catch (ObjectDisposedException) { /* same as above */ }
			});
		}

		bool lastIsBuiltin = isBuiltin[stageCount - 1];

		if (isBackground) {
			// A background pipeline is tracked via its last external stage, if
			// any. A pipeline whose last stage is a builtin has no process to
			// track (builtins are synchronous and already finished by now).
			if (!lastIsBuiltin) {
				var lastProcess = processes[stageCount - 1]!;
				int jobId = BackgroundJobManager.AddJob(lastProcess, command);
				Console.WriteLine($"[{jobId}] {lastProcess.Id}");
			}

			return;
		}

		if (lastIsBuiltin) {
			// The last stage is a builtin: its output was computed eagerly and
			// simply needs to be printed now that the pipeline has run.
			Console.Write(builtinOutputs[stageCount - 1]);
			return;
		}

		// Last stage is external: only it is awaited, so an earlier still-running
		// stage (e.g. "tail -f") does not block the shell.
		var finalProcess = processes[stageCount - 1]!;
		finalProcess.WaitForExit();

		if (lastStdoutMode != RedirectMode.None) {
			string output = finalProcess.StandardOutput.ReadToEnd();
			_WriteOutput(output, lastStdoutMode, lastStdoutFile);
		}

		if (finalProcess.StartInfo.RedirectStandardError) {
			string error = finalProcess.StandardError.ReadToEnd();
			Console.Error.Write(error);
		}
	}

	/// <summary>
	/// Parses a single command's tokens into a ready-to-start ProcessStartInfo,
	/// extracting any redirection operators (>, >>, 1>, 1>>, 2>, 2>>) out of the
	/// argument list. Used both for standalone commands and for each stage of a
	/// pipeline
	/// </summary>
	/// <param name="tokens">Separated list of command-line tokens</param>
	/// <param name="stdoutMode">Behavior for handling stdout redirection</param>
	/// <param name="stdoutFile">File to redirect stdout to, if applicable</param>
	/// <param name="stderrMode">Behavior for handling stderr redirection</param>
	/// <param name="stderrFile">File to redirect stderr to, if applicable</param>
	/// <returns></returns>
	private static ProcessStartInfo _BuildProcessStartInfo(
		List<string> tokens,
		out RedirectMode stdoutMode,
		out string stdoutFile,
		out RedirectMode stderrMode,
		out string stderrFile)
	{
		stdoutMode = RedirectMode.None;
		stderrMode = RedirectMode.None;
		stdoutFile = string.Empty;
		stderrFile = string.Empty;

		string commandName = tokens[0];

		var psi = new ProcessStartInfo {
			FileName = commandName,
			UseShellExecute = false
		};

		// Start at 1: tokens[0] is the command name, already used as FileName,
		// and must not be re-added as an argument.
		for (int i = 1; i < tokens.Count; i++) {
			string arg = tokens[i];

			if (arg == "1>" || arg == ">") { stdoutMode = RedirectMode.Overwrite; stdoutFile = tokens[++i]; continue; }
			if (arg == "1>>" || arg == ">>") { stdoutMode = RedirectMode.Append; stdoutFile = tokens[++i]; continue; }
			if (arg == "2>") { stderrMode = RedirectMode.Overwrite; stderrFile = tokens[++i]; continue; }
			if (arg == "2>>") { stderrMode = RedirectMode.Append; stderrFile = tokens[++i]; continue; }

			psi.ArgumentList.Add(arg);
		}

		return psi;
	}

	/// <summary>
	/// Builds the output of the "echo" builtin as a string, without printing it
	/// or handling its own redirection — used when echo is a pipeline stage,
	/// where the produced text is routed to the next stage (or the console)
	/// by the pipeline logic itself..
	/// </summary>
	/// <param name="echoArguments"></param>
	/// <returns></returns>
	private static string _GetEchoOutput(string echoArguments)
	{
		var echoTokens = _ParseArguments(echoArguments);
		var echoArgs = new List<string>();

		// Redirection operators are stripped even inside a pipeline stage, but
		// where they point to is ignored here: the pipeline decides where this
		// stage's output goes.
		for (int i = 0; i < echoTokens.Count; i++) {
			string arg = echoTokens[i];

			if (arg == ">" || arg == "1>" || arg == ">>" || arg == "1>>" ||
				arg == "2>" || arg == "2>>") {
				i++; // skip the filename that follows
				continue;
			}

			echoArgs.Add(arg);
		}

		return string.Join(" ", echoArgs) + "\n";
	}

	/// <summary>
	/// Builds the output of the "type" builtin as a string.
	/// </summary>
	/// <param name="specifiedCommand"></param>
	/// <param name="builtInCommands"></param>
	/// <returns></returns>
	private static string _GetTypeOutput(string specifiedCommand, string[] builtInCommands)
	{
		if (builtInCommands.Contains(specifiedCommand))
			return $"{specifiedCommand} is a shell builtin\n";

		string path = _FindCommandInPath(specifiedCommand);

		return string.IsNullOrEmpty(path)
			? $"{specifiedCommand}: not found\n"
			: $"{specifiedCommand} is {path}\n";
	}

	/// <summary>
	/// Runs an Action while temporarily capturing anything it writes to
	/// Console.Out, returning the captured text instead of printing it.
	/// Used to reuse existing builtins (like BackgroundJobManager.PrintJobs)
	/// inside a pipeline without rewriting them to return strings directly.
	/// </summary>
	/// <param name="action"></param>
	/// <returns></returns>
	private static string _CaptureConsoleOutput(Action action)
	{
		var originalOut = Console.Out;
		var stringWriter = new StringWriter();

		try {
			Console.SetOut(stringWriter);
			action();
		}
		finally {
			Console.SetOut(originalOut);
		}

		return stringWriter.ToString();
	}

	/// <summary>
	/// Executes a single builtin as one stage of a pipeline and returns
	/// everything it would have printed to stdout, as a string.
	/// </summary>
	/// <param name="stageText"></param>
	/// <param name="builtInCommands"></param>
	/// <returns></returns>
	private static string _ExecuteBuiltinForPipeline(string stageText, string[] builtInCommands)
	{
		var tokens = _ParseArguments(stageText);
		string commandName = tokens[0];

		switch (commandName) {
			case "echo":
				string echoArguments = stageText.Length > 4 ? stageText.Substring(5) : string.Empty;
				return _GetEchoOutput(echoArguments);

			case "type":
				string specifiedCommand = tokens.Count > 1 ? tokens[1] : string.Empty;
				return _GetTypeOutput(specifiedCommand, builtInCommands);

			case "pwd":
				return Directory.GetCurrentDirectory() + "\n";

			case "jobs":
				return _CaptureConsoleOutput(() => BackgroundJobManager.PrintJobs());

			default:
				return string.Empty;
		}
	}
}

