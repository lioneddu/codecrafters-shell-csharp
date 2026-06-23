using System.Diagnostics;
using System.Security;
using System.Text;

class Program
{
	static void Main()
	{
		while (true) {
			Console.Write("$ ");
			string command = Console.ReadLine();

			if (command.StartsWith("echo")) {
				string echoArguments = command.Substring(5);
				var arguments = _ParseArguments(echoArguments);

				int stdoutRedirect = arguments.IndexOf(">");
				if (stdoutRedirect == -1)
					stdoutRedirect = arguments.IndexOf("1>");

				int stderrRedirect = arguments.IndexOf("2>");

				string output = string.Empty;

				int stopIndex = arguments.Count;

				if (stdoutRedirect >= 0)
					stopIndex = Math.Min(stopIndex, stdoutRedirect);

				if (stderrRedirect >= 0)
					stopIndex = Math.Min(stopIndex, stderrRedirect);

				output = string.Join(" ", arguments.Take(stopIndex));

				if (stdoutRedirect >= 0) {
					string fileName = arguments[stdoutRedirect + 1];
					File.WriteAllText(fileName, output + "\n");
				}
				else {
					Console.WriteLine(output);
				}

				if (stderrRedirect >= 0) {
					string fileName = arguments[stderrRedirect + 1];

					File.WriteAllText(fileName, string.Empty);
				}

				continue;
			}

			if (command.StartsWith("type")) {
				string specifiedCommand = command.Split(" ")[1];
				string[] builtInCommands = new string[] { "echo", "exit", "type", "pwd", "cd" };

				if (builtInCommands.Contains(specifiedCommand.Trim())) {
					Console.WriteLine($"{specifiedCommand} is a shell builtin");
				}
				else {
					string path = _FindCommandInPath(specifiedCommand);
					if (string.IsNullOrEmpty(path)) {
						Console.WriteLine($"{specifiedCommand}: not found");
					}
					else {
						Console.WriteLine($"{specifiedCommand} is {path}");
					}
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
