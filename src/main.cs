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

				if (arguments.Contains(">")) {
					int operatorIndex = arguments.IndexOf(">");
					string outputFile = arguments[operatorIndex + 1];

					File.WriteAllText(outputFile, string.Join(" ", arguments[..operatorIndex]));
				}
				else {
					Console.WriteLine(string.Join(" ", arguments));
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
				bool redirectOutput = false;
				foreach (var arg in tokens.Skip(1)) {

					if (arg == ">") {
						redirectOutput = true;
						continue;
					}

					if (redirectOutput && arg != ">") {
						outputFilename = arg;
						break;
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

				if (redirectOutput && !string.IsNullOrEmpty(outputFilename)) {
					File.WriteAllText(outputFilename, output);
				}
				else {
					Console.Write(output);
				}

				if (!string.IsNullOrEmpty(error)) {
					Console.Error.Write(error);
				}
			}
			else {
				Console.WriteLine($"{command}: command not found");
			}

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
