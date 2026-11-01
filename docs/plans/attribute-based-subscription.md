# 用特性标记订阅者

## 要解决的问题

现在模块要在 `OnRegistering` 里手写每一条订阅：

```csharp
e.Registry.Subscribe<StorageChangedEvent>(OnStorageChanged);
```

Minecraft 的模组是在方法上加 `@SubscribeEvent` 注解，由加载器在运行时用反射找出这些方法。Runtime 要支持 NativeAOT，不能这样在运行时用反射扫描。

## 目前的想法

用项目里已有的源代码生成器，在编译时找出标了特性的方法，自动生成上面的 `Subscribe` 调用：

```csharp
[Subscribe]
private ValueTask OnStorageChanged(StorageChangedEvent e, EventContext context) { ... }
```

这样不需要反射，仍然符合 NativeAOT 的要求。

## 为什么现在不做

第一版模块少，手写调用就够了。等模块变多、手写变得繁琐时再做。
