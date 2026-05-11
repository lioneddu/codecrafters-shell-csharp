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
					Console.WriteLine($"'{specifiedCommand}' is a shell builtin");
				}
				else {
					Console.WriteLine($"{specifiedCommand}: command not found");
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
