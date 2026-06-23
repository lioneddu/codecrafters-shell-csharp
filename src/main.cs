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
				
				Console.WriteLine(string.Join(" ", _ParseArguments(echoArguments)));
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

				foreach (var arg in tokens.Skip(1)) {
					psi.ArgumentList.Add(arg);
				}

				psi.RedirectStandardOutput = true;
				psi.UseShellExecute = false;

				using var process = Process.Start(psi);

				string output = process.StandardOutput.ReadToEnd();

				process.WaitForExit();

				Console.Write(output);
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

		foreach (char c in input) {
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

		if (current.Length > 0)
			args.Add(current.ToString());

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
