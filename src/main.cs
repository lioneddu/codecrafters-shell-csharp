using System.Collections;
using System.Diagnostics;
using System.Security;
using System.Text;

class Program
{
	// Path used to restore the terminal to its original settings on exit.
	private static string _savedTtySettings = string.Empty;

	static void Main()
	{
		_EnableRawTerminalMode();

		try {

			string[] builtInCommands = { "echo", "exit", "type", "pwd", "cd", "complete", "jobs" };
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
					_ExecutePipeline(command, isBackground);
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

				// real command management
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
	/// Executes a pipeline of external commands connected by '|'. Standard output
	/// of each stage feeds standard input of the next. If isBackground is true,
	/// the whole pipeline is registered as a single job and the shell returns to
	/// the prompt immediately without waiting for the last stage.
	/// </summary>
	/// <param name="command"></param>
	/// <param name="isBackground"></param>
	private static void _ExecutePipeline(string command, bool isBackground)
	{
		var stageTexts = _SplitPipelineStages(command);
		var processes = new Process[stageTexts.Count];
		var stdoutModes = new RedirectMode[stageTexts.Count];

		RedirectMode lastStdoutMode = RedirectMode.None;
		string lastStdoutFile = string.Empty;

		for (int i = 0; i < stageTexts.Count; i++) {
			var tokens = _ParseArguments(stageTexts[i]);

			if (tokens.Count == 0) {
				Console.WriteLine("syntax error: empty pipeline stage");
				return;
			}

			if (string.IsNullOrEmpty(_FindCommandInPath(tokens[0]))) {
				Console.WriteLine($"{tokens[0]}: command not found");
				return;
			}

			var psi = _BuildProcessStartInfo(
				tokens,
				out RedirectMode stdoutMode, out string stdoutFile,
				out RedirectMode stderrMode, out string stderrFile);

			stdoutModes[i] = stdoutMode;
			bool isFirst = i == 0;
			bool isLast = i == stageTexts.Count - 1;

			if (isLast) {
				lastStdoutMode = stdoutMode;
				lastStdoutFile = stdoutFile;
			}

			psi.RedirectStandardInput = !isFirst;
			psi.RedirectStandardOutput = true;
			psi.RedirectStandardError = stderrMode != RedirectMode.None;

			var process = Process.Start(psi);
			processes[i] = process;

		}

		for (int i = 0; i < processes.Length - 1; i++) {
			if (stdoutModes[i] != RedirectMode.None)
				continue;

			int index = i;

			Task.Run(() =>
			{
				try {
					var source = processes[index].StandardOutput.BaseStream;
					var dest = processes[index + 1].StandardInput.BaseStream;

					byte[] buffer = new byte[4096];

					int read;

					while ((read = source.Read(buffer, 0, buffer.Length)) > 0) {
						dest.Write(buffer, 0, read);
						dest.Flush();
					}

					processes[index + 1].StandardInput.Close();
				}
				catch (IOException) {
				}
				catch (ObjectDisposedException) {
				}
			});
		}

		if (isBackground) {
			var lastProcess = processes[^1];
			int jobId = BackgroundJobManager.AddJob(lastProcess, command);

			Console.WriteLine($"[{jobId}] {lastProcess.Id}");
			return;
		}

		string output = processes[^1].StandardOutput.ReadToEnd();
		string error = string.Empty;

		if (processes[^1].StartInfo.RedirectStandardError) {
			error = processes[^1].StandardError.ReadToEnd();
		}

		processes[^1].WaitForExit();

		_WriteOutput(output, lastStdoutMode, lastStdoutFile);

		if (!string.IsNullOrEmpty(error)) {
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
}

