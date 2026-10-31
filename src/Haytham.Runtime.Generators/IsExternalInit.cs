namespace System.Runtime.CompilerServices;

// netstandard2.0 里没有这个类型，但 C# 编译器实现 init 访问器时需要它，
// 而每个位置式 record（例如 record Foo(string Name)）的属性都会用到 init。
// 在这里自己声明一个，生成器项目就能使用 record 了。
internal static class IsExternalInit {
}
