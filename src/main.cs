using System.Diagnostics;
using System.Security;

class Program
{
	static void Main()
	{
		while (true) {
			Console.Write("$ ");
			string command = Console.ReadLine();

			if (command.StartsWith("echo")) {
				Console.WriteLine(command.Substring(5));
				continue;
			}

			if (command.StartsWith("type")) {
				string specifiedCommand = command.Split(" ")[1];
				string[] builtInCommands = new string[] { "echo", "exit", "type", "pwd" };

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

			if(command.StartsWith("pwd")) {
				Console.WriteLine(Directory.GetCurrentDirectory());
				continue;
			}

			if (command.Equals("exit")) {
				break;

			}
			// real command management
			string[] args = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			string commandName = args[0];
			string commandPath = _FindCommandInPath(commandName);
			if (!string.IsNullOrEmpty(commandPath)) {
				Process.Start(commandName, args[1..]).WaitForExit();
			} else {
				Console.WriteLine($"{command}: command not found");
			}
	
		}

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
