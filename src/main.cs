using System.Collections;
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
				string[] builtInCommands = new string[] { "echo", "exit", "type" };

				if (builtInCommands.Contains(specifiedCommand.Trim())) {
					Console.WriteLine($"{specifiedCommand} is a shell builtin");
				}
				else {
					try {

						foreach (DictionaryEntry de in Environment.GetEnvironmentVariables()) {
							Console.WriteLine($"{de.Key}={de.Value}");
							string environmentVariablePath = Path.GetFileName(de.Key.ToString());
							if (File.Exists(environmentVariablePath)) {
								Console.WriteLine($"{specifiedCommand}: is {environmentVariablePath}");
								break;
							}
							else {
								continue;
							}

						}


						Console.WriteLine($"{specifiedCommand}: not found");
						break;

					}
					catch (SecurityException exception) {
						Console.WriteLine($"{specifiedCommand}: not found");
					}
				}

				continue;
			}

			if (command.Equals("exit")) {
				break;
			}
			Console.WriteLine($"{command}: command not found");
		}
	}
}
