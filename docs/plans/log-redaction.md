# 发行版日志里的异常详情和脱敏

## 要解决的问题

现在 Runtime 的日志在 Information 及以上级别只记录类型名、状态和计数，不记录异常消息、堆栈和 Task 的输入。异常对象只在 Debug 级别的 1104 记录里交给 logger，而发行版不能开启 Debug 级别。规则见[开发约束](../development.md#日志与机密)。

这样做的原因是 Runtime 不知道模块或外部库会在异常消息里放什么，可能带上 token 或游戏命令行。副作用是玩家贴来的发行版日志只能帮忙缩小范围。例如安装时下载失败，日志大致是：

```text
[Error] 1100 Execution failure observed; kind Execution; exception type System.Net.Http.HttpRequestException.
        RuntimeId=…  ModuleId=Gebinde.Install  Phase=…  TaskType=Gebinde.Install.InstallTask  ExecutionId=…
[Error] 2002 Task finished with outcome Failed, 1 business failures, and output failure False.
```

从这里能知道是哪个模块、哪种 Task 出错，以及异常的类型；但看不到异常消息（例如哪个网址返回了 404）、堆栈（错在哪一行），也看不到装的是哪个实例、哪个版本。排查时往往还要请玩家重现。

## 目前想到的做法

1. **Error 级别直接带异常对象，由 HL 的日志输出端统一脱敏。** HL 的 C 接口文档（`c-interface-basics.md` 的凭据一条）提过：错误的 `message` 在输出前过一遍脱敏，处理内置的 CF key、`access_token=`、`refresh_token=`、`Bearer ` 和代理网址里的用户名与密码。如果这套脱敏做出来，日志输出端在写文件前调用同一个函数，就能让发行版日志带上完整的消息和堆栈，脱敏规则也只维护一份。Runtime 这边只需要给 1100 到 1103 加上异常参数，再删掉 1104。要注意的是，输出端漏接脱敏时，机密会直接写进日志。
2. **Error 级别只多记堆栈，不记消息。** 堆栈里主要是方法名，不带玩家的数据。但在 JIT 下有 PDB 时，堆栈会带上构建机器上的源码路径，例如 `C:\Users\<开发者账号>\…`，同样要决定要不要遮掉。这个做法也看不到 404 这类具体原因。
3. **业务细节由 HL 和模块自己记录。** Runtime 不知道“实例”“版本”的含义，但 HL 在 `hl_start` 时知道，模块在处理时也知道。它们可以用 Information 级别记录不含机密的字段，例如 job id、op 名称、实例 Id。这一条和前两条不冲突，选哪种都应该做。

目前倾向 1 加 3，因为 HL 是唯一的使用方，脱敏函数本来就要为错误 JSON 做，日志只是再用一次。

## 为什么现在不做

- HL 还没有接入 Runtime，Runtime 的日志现在只有 Test 项目在用，玩家还碰不到这个问题。
- HL 文档里的脱敏只是提案，还没有实现。哪些字符串算机密、账号凭据放在哪里，和 HL 的 T6（启动核心与账号的设计）有关，T6 还没有定案。现在设计脱敏，只能自己假设机密有哪些。
- Runtime 这边要改的部分很小（见做法 1），晚一点决定不会让 Runtime 返工。

## 什么时候拿出来

HL 准备把 Runtime 的日志接到发行版之前，并且要等 T6 定案。拿出来时：

- 先确认 HL 的脱敏函数是否已经实现、覆盖哪些内容。
- 在做法 1、2 之间选一个，交给 Astra 设计 Runtime 和 HL 输出端的分工，同时把做法 3 要记录的字段列出来。
- 选定以后，同步修改开发约束的“日志与机密”一节，以及 1100 到 1104 的记录点。
