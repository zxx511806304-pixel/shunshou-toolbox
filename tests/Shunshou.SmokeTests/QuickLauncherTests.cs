using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class QuickLauncherTests
{
    public static Task RunAsync(string root)
    {
        // Operator precedence and parentheses
        CompressionTests.Check(QuickCalculateService.TryEvaluate("1+2*3", out double r) && r == 7, "应先乘除后加减");
        CompressionTests.Check(QuickCalculateService.TryEvaluate("(1+2)*3", out r) && r == 9, "括号应改变优先级");
        CompressionTests.Check(QuickCalculateService.TryEvaluate("10-4-3", out r) && r == 3, "减法应左结合");
        Console.WriteLine("PASS quick-launcher: precedence");

        // Power is right-associative; unary signs; modulo; decimal division
        CompressionTests.Check(QuickCalculateService.TryEvaluate("2^10", out r) && r == 1024, "乘方");
        CompressionTests.Check(QuickCalculateService.TryEvaluate("2^3^2", out r) && r == 512, "乘方应右结合（2^3^2=512）");
        CompressionTests.Check(QuickCalculateService.TryEvaluate("-3+5", out r) && r == 2, "一元负号");
        CompressionTests.Check(QuickCalculateService.TryEvaluate("10%4", out r) && r == 2, "取余");
        CompressionTests.Check(QuickCalculateService.TryEvaluate("7/2", out r) && r == 3.5, "小数除法");
        Console.WriteLine("PASS quick-launcher: operators");

        // Chinese punctuation and thousands separators
        CompressionTests.Check(QuickCalculateService.TryEvaluate("3×4", out r) && r == 12, "中文乘号");
        CompressionTests.Check(QuickCalculateService.TryEvaluate("8÷2", out r) && r == 4, "中文除号");
        CompressionTests.Check(QuickCalculateService.TryEvaluate("（1+2）×3", out r) && r == 9, "中文括号");
        CompressionTests.Check(QuickCalculateService.TryEvaluate("1,024+1", out r) && r == 1025, "千分位逗号");
        Console.WriteLine("PASS quick-launcher: punctuation");

        // Inputs that must not produce a result row
        CompressionTests.Check(!QuickCalculateService.TryEvaluate("12345", out _), "纯数字不应产生结果");
        CompressionTests.Check(!QuickCalculateService.TryEvaluate("-42", out _), "带符号纯数字不应产生结果");
        CompressionTests.Check(!QuickCalculateService.TryEvaluate("报告.docx", out _), "文件名不应产生结果");
        CompressionTests.Check(!QuickCalculateService.TryEvaluate("1/0", out _), "除以零不应产生结果");
        CompressionTests.Check(!QuickCalculateService.TryEvaluate("0/0", out _), "零除零不应产生结果");
        CompressionTests.Check(!QuickCalculateService.TryEvaluate("1+", out _), "不完整表达式不应产生结果");
        CompressionTests.Check(!QuickCalculateService.TryEvaluate("(1+2", out _), "括号不配对不应产生结果");
        CompressionTests.Check(!QuickCalculateService.TryEvaluate("1..2+3", out _), "非法数字不应产生结果");
        CompressionTests.Check(!QuickCalculateService.TryEvaluate("1+2xyz", out _), "尾随垃圾不应产生结果");
        CompressionTests.Check(!QuickCalculateService.TryEvaluate("", out _), "空输入不应产生结果");
        Console.WriteLine("PASS quick-launcher: rejection");

        // Result formatting for the row and the clipboard
        CompressionTests.Check(QuickCalculateService.Format(3.0) == "3", "整数格式化");
        CompressionTests.Check(QuickCalculateService.Format(3.5) == "3.5", "小数格式化");
        CompressionTests.Check(QuickCalculateService.Format(1.0 / 3) == "0.3333333333", "长小数格式化");
        Console.WriteLine("PASS quick-launcher: formatting");
        return Task.CompletedTask;
    }
}
