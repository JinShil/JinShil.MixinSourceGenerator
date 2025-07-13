# JinShil.MixinSourceGenerator
A C# source generator for composing classes or structs from other classes or structs using mixins.

# Example

By adding the `Mixin` attribute to `Composition`, the source generator mixes in the contents of `Implementation1` and `Implementation2`, including any XML comments and attributes, verbatim into `Composition`, 

```C#
class Implementation1
{
    /// <summary>
    /// Summary for Property1
    /// </summary>
    [SomeAttribute]
    public int Property1 { get; }

    /// <summary>
    /// Summary for Method1
    /// </summary>
    [SomeAttribute]
    public void Method1()
    {
        Console.WriteLine("Running Method 1");
    }
}
```

```C#
class Implementation2
{
    /// <summary>
    /// Summary for Property2
    /// </summary>
    [SomeAttribute]
    public int Property2 { get; }

    /// <summary>
    /// Summary for Method2
    /// </summary>
    [SomeAttribute]
    public void Method2()
    {
        Console.WriteLine("Running Method 2");
    }
}
```

```C#
[Mixin(typeof(Implementation1))]
[Mixin(typeof(Implementation2))]
partial class Composition : SomeBaseClass, ISomeInterface
{
     /// <summary>
    /// Summary for Property1
    /// </summary>
    [SomeAttribute]
    public int Property1 { get; }

    /// <summary>
    /// Summary for Method1
    /// </summary>
    [SomeAttribute]
    public void Method1()
    {
        Console.WriteLine("Running Method 1");
    }

    /// <summary>
    /// Summary for Property2
    /// </summary>
    [SomeAttribute]
    public int Property2 { get; }

    /// <summary>
    /// Summary for Method2
    /// </summary>
    [SomeAttribute]
    public void Method2()
    {
        Console.WriteLine("Running Method 2");
    }
}
```
