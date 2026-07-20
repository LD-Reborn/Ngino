namespace Ngino.Protocol;

public sealed class HeaderPair
{
    public HeaderPair()
    {
    }

    public HeaderPair(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; set; } = "";

    public string Value { get; set; } = "";
}
