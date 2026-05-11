class Program
{
    static void Main()
    {
        while (true) {
            Console.Write("$ ");
            string command = Console.ReadLine();
            
            if(command.StartsWith("echo ")) {
                Console.WriteLine(command.Substring(5));
            }

            if (command.Equals("exit")) {
                break;
            }
            Console.WriteLine($"{command}: command not found");
        }
	}
}
