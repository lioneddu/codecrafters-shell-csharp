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

						string path = Environment.GetEnvironmentVariable("PATH");

						if (!string.IsNullOrEmpty(path)) {

							foreach (string dir in path.Split(':')) {
								string fullPath = Path.Combine(dir, specifiedCommand);
								if (File.Exists(fullPath)) {
									Console.WriteLine($"{specifiedCommand} is {fullPath}");
									break;
								}
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
