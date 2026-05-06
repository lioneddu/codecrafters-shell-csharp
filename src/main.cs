class Program
{
    static void Main()
    {
        while (true) {
            Console.Write("$ ");
            string command = Console.ReadLine();
            Console.WriteLine($"{command}: command not found");
        }
	}
}
