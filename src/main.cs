using System.Diagnostics;
using System.Security;
using System.Text;

class Program
{
	static void Main()
	{
		string[] builtInCommands = { "echo", "exit", "type", "pwd", "cd", "complete" };
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
			Console.Write("$ ");

			string command = _ReadCommandLine(allCommands, customCompletions);
			//string command = Console.ReadLine();

			if (command.StartsWith("echo")) {
				string echoArguments = command.Substring(5);
				var arguments = _ParseArguments(echoArguments);

				RedirectMode stdoutMode = RedirectMode.None;
				RedirectMode stderrMode = RedirectMode.None;

				string stdoutFile = string.Empty;
				string stderrFile = string.Empty;

				var echoArgs = new List<string>();

				for (int i = 0; i < arguments.Count; i++) {
					string arg = arguments[i];

					if (arg == ">" || arg == "1>") {
						stdoutMode = RedirectMode.Overwrite;
						stdoutFile = arguments[++i];
						continue;
					}

					if (arg == ">>" || arg == "1>>") {
						stdoutMode = RedirectMode.Append;
						stdoutFile = arguments[++i];
						continue;
					}

					if (arg == "2>") {
						stderrMode = RedirectMode.Overwrite;
						stderrFile = arguments[++i];
						continue;
					}

					if (arg == "2>>") {
						stderrMode = RedirectMode.Append;
						stderrFile = arguments[++i];
						continue;
					}

					echoArgs.Add(arg);
				}

				string output = string.Join(" ", echoArgs) + "\n";

				if (stdoutMode == RedirectMode.None) {
					Console.Write(output);
				}
				else {
					_WriteOutput(output, stdoutMode, stdoutFile);
				}

				if (stderrMode != RedirectMode.None) {
					_WriteOutput(string.Empty, stderrMode, stderrFile);
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
				}
				continue;
			}


			if (command.Equals("exit")) {
				break;

			}

			// real command management
			var tokens = _ParseArguments(command);

			string commandName = tokens[0];

			string commandPath = _FindCommandInPath(commandName);
			if (!string.IsNullOrEmpty(commandPath)) {
				var psi = new ProcessStartInfo();
				psi.FileName = commandName; //The system resolves the path, so we can just use the command name

				string outputFilename = string.Empty;
				string errorFilename = string.Empty;
				RedirectMode stdoutMode = RedirectMode.None;
				RedirectMode stderrMode = RedirectMode.None;


				for (int i = 1; i < tokens.Count; i++) {
					var arg = tokens[i];

					if (arg == "1>" || arg == ">") {
						stdoutMode = RedirectMode.Overwrite;
						outputFilename = tokens[++i];
						continue;
					}

					if (arg == "1>>" || arg == ">>") {
						stdoutMode = RedirectMode.Append;
						outputFilename = tokens[++i];
						continue;
					}

					if (arg == "2>") {
						stderrMode = RedirectMode.Overwrite;
						errorFilename = tokens[++i];
						continue;
					}

					if (arg == "2>>") {
						stderrMode = RedirectMode.Append;
						errorFilename = tokens[++i];
						continue;
					}

					psi.ArgumentList.Add(arg);
				}

				psi.RedirectStandardOutput = true;
				psi.RedirectStandardError = true;
				psi.UseShellExecute = false;

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
			else {
				Console.WriteLine($"{command}: command not found");
			}

		}


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

						using var process = Process.Start(psi);

						string completion = process.StandardOutput.ReadToEnd()
							.TrimEnd('\r', '\n');

						process.WaitForExit();

						// If the completer printed nothing, there are no candidates:
						// ring the bell and leave the input line untouched.
						if (string.IsNullOrEmpty(completion)) {
							Console.Write("\x07");
							previousWasTab = false;
							continue;
						}

						matches = new List<string> { completion };
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
}
