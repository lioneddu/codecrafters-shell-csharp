class Program
{
    static void Main()
    {
        while (true) {
            Console.Write("$ ");
            string command = Console.ReadLine();
            if (command.Equals("exit")) {
                break;
            }
            Console.WriteLine($"{command}: command not found");
        }
	}
}
