using System.Diagnostics;
using System.Security;
using System.Text;

class Program
{
	static void Main()
	{
		string[] builtInCommands =	{"echo","exit",	"type",	"pwd",	"cd"};

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

			string command = _ReadCommandLine(allCommands);
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


				foreach (var arg in tokens.Skip(1)) {

					if (arg == ">" || arg == "1>") {
						stdoutMode = RedirectMode.Overwrite;
						continue;
					}

					if (arg == ">>" || arg == "1>>") {
						stdoutMode = RedirectMode.Append;
						continue;
					}


					if (arg == "2>") {
						stderrMode = RedirectMode.Overwrite;
						continue;
					}

					if (arg == "2>>") {
						stderrMode = RedirectMode.Append;
						continue;
					}

					if (stdoutMode != RedirectMode.None && arg != ">" && arg != "1>") {
						outputFilename = arg;
						continue;
					}

					if (stderrMode != RedirectMode.None && arg != "2>") {
						errorFilename = arg;
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


				if (stderrMode != RedirectMode.None && !string.IsNullOrEmpty(errorFilename)) {
					_WriteOutput(error, stderrMode, errorFilename);
				}
				else {
					Console.Error.Write(error);
				}
			}
			else {
				Console.WriteLine($"{command}: command not found");
			}

		}


	}

	private static string _ReadCommandLine(string[] allCommands)
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

				if (lastSpace == -1) {
					// Command completion
					prefix = line;


					matches = allCommands
						.Where(c => c.StartsWith(prefix))
						.OrderBy(c => c)
						.ToList();
				}
				else {
					// Filename completion
					prefix = line[(lastSpace + 1)..];
					
					string directory;
					string partialName;

					if (prefix.Contains('/')) {
						directory = Path.GetDirectoryName(prefix);
						partialName = Path.GetFileName(prefix);

						if (string.IsNullOrEmpty(directory))
							directory = "/";
					}
					else {
						directory = Directory.GetCurrentDirectory();
						partialName = prefix;
					}

					matches = Directory
						.GetFileSystemEntries(directory)
						.Select(Path.GetFileName)
						.Where(f => f.StartsWith(partialName))
						.OrderBy(f => f)
						.ToList();
				}

				Complete(matches, prefix, buffer, ref previousWasTab);
				continue;
			}

			previousWasTab = false;
			buffer.Append(key.KeyChar);
			Console.Write(key.KeyChar);
		}
	}

	private static void Complete(
	List<string> matches,
	string prefix,
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
			buffer.Append(' ');

			Console.Write(missing + " ");

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
		Console.WriteLine(string.Join("  ", matches));

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
