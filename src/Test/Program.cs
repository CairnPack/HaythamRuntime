Console.WriteLine("Chapter 1: consumer flow sketch.");

/*
 1. 创建组装入口。
 var builder = new RuntimeBuilder();
 提供配置，并接入由消费者持有的日志设施。

 2. 将外部准备好的模块实例，作为参数传入通用注册入口。
 builder.AddModule(storageModule);
 builder.AddModule(installModule);

 3. 校验并冻结注册信息，然后创建 Runtime 实例。
 var runtime = builder.Build();

 4. 等待加载与启动成功完成。
 await runtime.StartAsync();

 5. 提交安装请求，并取得它的实际执行结果。
 通过 Runtime 管理的执行入口提交请求。

 6. 应用决定停止时，向 Runtime 提出停止请求。
 var stopResult = await runtime.RequestStopAsync();
 停止被否决时，Runtime 继续可用，应用不会因此结束。
 停止成功表示必须等待的工作与清理都已完成。
 失败时向调用方报告错误，不能假装 Runtime 已成功停止。
 
 7. 应用可通过 runtime.Kill() 强制结束线程
 与 电脑断电时/线程被强制关闭 时行为一致
 */