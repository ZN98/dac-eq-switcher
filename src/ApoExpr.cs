// DAC EQ Switcher - Copyright 2026 ZN98 - GPL-3.0-or-later (see LICENSE and NOTICE).
// Follows muParserX (Copyright (c) Ingo Berg, BSD-2-Clause) as extended by Equalizer APO
// (Copyright (C) 2013 Jonas Thedering, GPL-2.0-or-later).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DacEqSwitcher
{
    sealed class ExprException : Exception { public ExprException(string m) : base(m) { } }

    /// Equalizer APO's expression language (muParserX 3 plus APO's additions): numbers, strings,
    /// booleans and arrays; + - * / ^, comparisons, and/or/not, bit operators, ?:, assignments,
    /// casts, units (n mu m k M G), the standard functions and regexSearch/regexReplace/readReg*.
    sealed class ApoExpr
    {
        public sealed class Val
        {
            public char T;          // f float, i int, b bool, s string, m array
            public double F;
            public string S;
            public Val[] A;

            public static Val Num(double v) { return new Val { T = 'f', F = v }; }
            public static Val Int(long v) { return new Val { T = 'i', F = v }; }
            public static Val Bool(bool v) { return new Val { T = 'b', F = v ? 1 : 0 }; }
            public static Val Str(string v) { return new Val { T = 's', S = v }; }
            public static Val Arr(Val[] v) { return new Val { T = 'm', A = v }; }

            public bool IsNum { get { return T == 'f' || T == 'i' || T == 'b'; } }

            public double Number(string op)
            {
                if (!IsNum) throw new ExprException("a number was expected for " + op);
                return F;
            }

            public bool Truth
            {
                get
                {
                    switch (T)
                    {
                        case 'b': case 'i': return F != 0;
                        case 'f': return F != 0 && !double.IsNaN(F);
                        case 's': return S.Length > 0 && S != "false" && S != "0";
                        case 'm': return A.Length > 0;
                    }
                    return false;
                }
            }

            /// How Equalizer APO turns a value into text for inline expressions.
            public string Text
            {
                get
                {
                    switch (T)
                    {
                        case 's': return S;
                        case 'b': return F != 0 ? "true" : "false";
                        case 'i': return ((long)F).ToString(CultureInfo.InvariantCulture);
                        case 'm': return string.Join(", ", A.Select(a => a.T == 's' ? "\"" + a.S + "\"" : a.Text));
                        default: return FormatG(F);
                    }
                }
            }
        }

        /// C++ ostream with precision 15 (%.15g).
        public static string FormatG(double v)
        {
            if (double.IsNaN(v)) return "nan";
            if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
            if (v == 0) return "0";
            int exp = (int)Math.Floor(Math.Log10(Math.Abs(v)));
            if (exp < -5 || exp >= 15)
            {
                string m = (v / Math.Pow(10, exp)).ToString("0.##############", CultureInfo.InvariantCulture);
                if (m == "10" || m == "-10") { exp++; m = m.Replace("10", "1"); }
                return m + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00");
            }
            return Math.Round(v, Math.Max(0, Math.Min(15, 14 - exp))).ToString("0.###############", CultureInfo.InvariantCulture);
        }

        readonly Dictionary<string, Val> consts = new Dictionary<string, Val>();
        readonly Dictionary<string, Val> vars = new Dictionary<string, Val>();

        public ApoExpr(double sampleRate, int inputChannels, int outputChannels, string stage, string deviceName, string connectionName, string deviceGuid)
        {
            consts["pi"] = Val.Num(Math.PI);
            consts["e"] = Val.Num(Math.E);
            consts["true"] = Val.Bool(true);
            consts["false"] = Val.Bool(false);
            consts["sampleRate"] = Val.Num(sampleRate);
            consts["inputChannelCount"] = Val.Int(inputChannels);
            consts["outputChannelCount"] = Val.Int(outputChannels);
            consts["stage"] = Val.Str(stage);
            consts["deviceName"] = Val.Str(deviceName ?? "");
            consts["connectionName"] = Val.Str(connectionName ?? "");
            consts["deviceGuid"] = Val.Str(deviceGuid ?? "");
        }

        /// Names of device constants an expression used (the graph can't know them for certain).
        public readonly HashSet<string> UsedDeviceConstants = new HashSet<string>();
        public bool UsedRegistry;

        public Val Eval(string text)
        {
            var p = new Parser(Tokenize(text));
            var node = p.Sequence();
            if (!p.AtEnd) throw new ExprException("unexpected \"" + p.Peek.Text + "\"");
            return node(this);
        }

        // ------------------------------------------------------------ tokens --
        sealed class Tok
        {
            public char K;   // n number, s string, i identifier, o operator, c cast, e end
            public string Text;
            public double Num;
        }

        static readonly string[] Ops = { "(float)", "(int)", "==", "!=", "<=", ">=", "<<", ">>", "&&", "||", "+=", "-=", "*=", "/=",
                                         "+", "-", "*", "/", "^", "<", ">", "&", "|", "=", "?", ":", ",", ";", "(", ")", "{", "}", "[", "]", "!", "%", "'" };

        static List<Tok> Tokenize(string s)
        {
            var t = new List<Tok>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < s.Length && s[i] != '"')
                    {
                        if (s[i] == '\\' && i + 1 < s.Length)
                        {
                            char n = s[++i];
                            sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n == 'r' ? '\r' : n);
                        }
                        else sb.Append(s[i]);
                        i++;
                    }
                    if (i >= s.Length) throw new ExprException("string is not closed");
                    i++;
                    t.Add(new Tok { K = 's', Text = sb.ToString() });
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    if (c == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                    {
                        int j = i + 2;
                        while (j < s.Length && Uri.IsHexDigit(s[j])) j++;
                        t.Add(new Tok { K = 'n', Text = s.Substring(i, j - i), Num = Convert.ToInt64(s.Substring(i + 2, j - i - 2), 16) });
                        i = j;
                        continue;
                    }
                    int k = i;
                    while (k < s.Length && (char.IsDigit(s[k]) || s[k] == '.')) k++;
                    if (k < s.Length && (s[k] == 'e' || s[k] == 'E'))
                    {
                        int m = k + 1;
                        if (m < s.Length && (s[m] == '+' || s[m] == '-')) m++;
                        if (m < s.Length && char.IsDigit(s[m])) { k = m; while (k < s.Length && char.IsDigit(s[k])) k++; }
                    }
                    string num = s.Substring(i, k - i);
                    double v;
                    if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) throw new ExprException("bad number " + num);
                    t.Add(new Tok { K = 'n', Text = num, Num = v });
                    i = k;
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int k = i;
                    while (k < s.Length && (char.IsLetterOrDigit(s[k]) || s[k] == '_')) k++;
                    t.Add(new Tok { K = 'i', Text = s.Substring(i, k - i) });
                    i = k;
                    continue;
                }
                string op = Ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
                if (op == null) throw new ExprException("unexpected character '" + c + "'");
                t.Add(new Tok { K = op[0] == '(' && op.Length > 1 ? 'c' : 'o', Text = op });
                i += op.Length;
            }
            t.Add(new Tok { K = 'e', Text = "end" });
            return t;
        }

        // ------------------------------------------------------------ parser --
        delegate Val Node(ApoExpr x);

        sealed class Parser
        {
            readonly List<Tok> t;
            int p;
            public Parser(List<Tok> tokens) { t = tokens; }
            public Tok Peek { get { return t[p]; } }
            public bool AtEnd { get { return t[p].K == 'e'; } }
            bool IsOp(string s) { return (t[p].K == 'o' || t[p].K == 'i') && t[p].Text == s; }
            Tok Next() { return t[p++]; }
            void Expect(string s) { if (!IsOp(s)) throw new ExprException("\"" + s + "\" expected"); p++; }

            /// a, b; c  -> evaluates all, returns the last
            public Node Sequence()
            {
                var list = new List<Node> { Assign() };
                while (IsOp(",") || IsOp(";")) { p++; if (AtEnd) break; list.Add(Assign()); }
                if (list.Count == 1) return list[0];
                return x => { Val v = null; foreach (var n in list) v = n(x); return v; };
            }

            Node Assign()
            {
                int save = p;
                if (t[p].K == 'i' && t[p + 1].K == 'o' && new[] { "=", "+=", "-=", "*=", "/=" }.Contains(t[p + 1].Text))
                {
                    string name = Next().Text, op = Next().Text;
                    var rhs = Assign();
                    return x =>
                    {
                        if (x.consts.ContainsKey(name)) throw new ExprException(name + " is a constant");
                        var v = rhs(x);
                        if (op != "=")
                        {
                            Val cur;
                            if (!x.vars.TryGetValue(name, out cur)) cur = Val.Num(0);
                            v = ApoExpr.Binary(op.Substring(0, 1), cur, v);
                        }
                        x.vars[name] = v;
                        return v;
                    };
                }
                p = save;
                return Ternary();
            }

            Node Ternary()
            {
                var c = Binary(1);
                if (!IsOp("?")) return c;
                p++;
                var a = Assign();
                Expect(":");
                var b = Assign();
                return x => c(x).Truth ? a(x) : b(x);
            }

            static int Prec(Tok k)
            {
                if (k.K != 'o' && k.K != 'i') return -1;
                switch (k.Text)
                {
                    case "||": case "or": case "xor": return 1;
                    case "&&": case "and": return 3;
                    case "|": return 4;
                    case "&": return 6;
                    case "==": case "!=": return 7;
                    case "<": case ">": case "<=": case ">=": return 8;
                    case "<<": case ">>": return 9;
                    case "+": case "-": return 11;
                    case "*": case "/": return 12;
                    case "^": return 13;
                }
                return -1;
            }

            Node Binary(int min)
            {
                var left = Unary();
                while (true)
                {
                    int pr = Prec(Peek);
                    if (pr < min) return left;
                    string op = Next().Text;
                    var right = op == "^" ? Binary(pr) : Binary(pr + 1);   // ^ is right-associative
                    var l = left;
                    if (op == "&&" || op == "and") left = x => Val.Bool(l(x).Truth && right(x).Truth);
                    else if (op == "||" || op == "or") left = x => Val.Bool(l(x).Truth || right(x).Truth);
                    else if (op == "xor") left = x => Val.Bool(l(x).Truth ^ right(x).Truth);
                    else left = x => ApoExpr.Binary(op, l(x), right(x));
                }
            }

            Node Unary()
            {
                if (IsOp("-") || IsOp("+"))
                {
                    bool neg = Next().Text == "-";
                    var a = Binary(12);   // signs bind looser than ^
                    return neg ? (Node)(x => { var v = a(x); return v.T == 'm' ? Val.Arr(v.A.Select(e => Val.Num(-e.Number("-"))).ToArray()) : v.T == 'i' ? Val.Int(-(long)v.F) : Val.Num(-v.Number("-")); }) : a;
                }
                if (IsOp("not"))
                {
                    p++;
                    var a = Binary(12);
                    return x => Val.Bool(!a(x).Truth);
                }
                if (Peek.K == 'c')
                {
                    bool toInt = Next().Text == "(int)";
                    var a = Binary(12);
                    return x => { double v = a(x).Number("cast"); return toInt ? Val.Int((long)Math.Truncate(v)) : Val.Num(v); };
                }
                return Postfix(Primary());
            }

            static readonly Dictionary<string, double> Units = new Dictionary<string, double>
            { { "n", 1e-9 }, { "mu", 1e-6 }, { "m", 1e-3 }, { "k", 1e3 }, { "M", 1e6 }, { "G", 1e9 } };

            Node Postfix(Node a)
            {
                while (true)
                {
                    if (IsOp("["))
                    {
                        p++;
                        var idx = Assign();
                        Expect("]");
                        var arr = a;
                        a = x =>
                        {
                            var v = arr(x);
                            int i = (int)idx(x).Number("index");
                            if (v.T != 'm' || i < 0 || i >= v.A.Length) throw new ExprException("index out of range");
                            return v.A[i];
                        };
                    }
                    else if (IsOp("!") && !(p + 1 < t.Count && t[p + 1].Text == "="))
                    {
                        p++;
                        var b = a;
                        a = x => { double v = b(x).Number("!"), r = 1; for (int i = 2; i <= v; i++) r *= i; return Val.Num(r); };
                    }
                    else if (IsOp("%"))
                    {
                        p++;
                        var b = a;
                        a = x => Val.Num(b(x).Number("%") / 100);
                    }
                    else if (Peek.K == 'i' && Units.ContainsKey(Peek.Text))
                    {
                        double f = Units[Next().Text];
                        var b = a;
                        a = x => Val.Num(b(x).Number("unit") * f);
                    }
                    else return a;
                }
            }

            Node Primary()
            {
                var k = Next();
                switch (k.K)
                {
                    case 'n': { var v = k.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Val.Int((long)k.Num) : Val.Num(k.Num); return x => v; }
                    case 's': { var v = Val.Str(k.Text); return x => v; }
                    case 'i':
                        {
                            string name = k.Text;
                            if (IsOp("("))
                            {
                                p++;
                                var args = new List<Node>();
                                if (!IsOp(")"))
                                {
                                    args.Add(Assign());
                                    while (IsOp(",")) { p++; args.Add(Assign()); }
                                }
                                Expect(")");
                                return x => x.Call(name, args.Select(n => n(x)).ToArray());
                            }
                            return x => x.Get(name);
                        }
                    case 'o':
                        if (k.Text == "(")
                        {
                            var e = Sequence();
                            Expect(")");
                            return e;
                        }
                        if (k.Text == "{")
                        {
                            var items = new List<Node>();
                            if (!IsOp("}"))
                            {
                                items.Add(Assign());
                                while (IsOp(",")) { p++; items.Add(Assign()); }
                            }
                            Expect("}");
                            return x => Val.Arr(items.Select(n => n(x)).ToArray());
                        }
                        break;
                }
                throw new ExprException("unexpected \"" + k.Text + "\"");
            }
        }

        Val Get(string name)
        {
            Val v;
            if (consts.TryGetValue(name, out v))
            {
                if (name == "deviceName" || name == "connectionName" || name == "deviceGuid") UsedDeviceConstants.Add(name);
                return v;
            }
            if (vars.TryGetValue(name, out v)) return v;
            vars[name] = v = Val.Num(0);   // created automatically, like Equalizer APO
            return v;
        }

        static Val Binary(string op, Val a, Val b)
        {
            if (op == "+" && (a.T == 's' || b.T == 's')) return Val.Str(a.Text + b.Text);
            if (op == "==" || op == "!=")
            {
                bool eq;
                if (a.T == 's' || b.T == 's')
                {
                    if (a.T != b.T) throw new ExprException("can't compare text with a number");
                    eq = a.S == b.S;
                }
                else eq = a.Number(op) == b.Number(op);
                return Val.Bool(op == "==" ? eq : !eq);
            }
            if (op == "<" || op == ">" || op == "<=" || op == ">=")
            {
                int c;
                if (a.T == 's' && b.T == 's') c = string.CompareOrdinal(a.S, b.S);
                else c = a.Number(op).CompareTo(b.Number(op));
                return Val.Bool(op == "<" ? c < 0 : op == ">" ? c > 0 : op == "<=" ? c <= 0 : c >= 0);
            }
            if (a.T == 'm' || b.T == 'm')
            {
                if (a.T == 'm' && b.T == 'm')
                {
                    if (a.A.Length != b.A.Length) throw new ExprException("arrays of different sizes");
                    return Val.Arr(a.A.Select((v, i) => Binary(op, v, b.A[i])).ToArray());
                }
                return a.T == 'm' ? Val.Arr(a.A.Select(v => Binary(op, v, b)).ToArray()) : Val.Arr(b.A.Select(v => Binary(op, a, v)).ToArray());
            }
            double x = a.Number(op), y = b.Number(op);
            bool ints = (a.T == 'i' || a.T == 'b') && (b.T == 'i' || b.T == 'b');
            switch (op)
            {
                case "+": return ints ? Val.Int((long)x + (long)y) : Val.Num(x + y);
                case "-": return ints ? Val.Int((long)x - (long)y) : Val.Num(x - y);
                case "*": return ints ? Val.Int((long)x * (long)y) : Val.Num(x * y);
                case "/": return Val.Num(x / y);
                case "^": return Val.Num(Math.Pow(x, y));
                case "&": return Val.Int((long)x & (long)y);
                case "|": return Val.Int((long)x | (long)y);
                case "<<": return Val.Int((long)x << (int)y);
                case ">>": return Val.Int((long)x >> (int)y);
            }
            throw new ExprException("unknown operator " + op);
        }

        static readonly Dictionary<string, Func<double, double>> Unary1 = new Dictionary<string, Func<double, double>>
        {
            { "sin", Math.Sin }, { "cos", Math.Cos }, { "tan", Math.Tan }, { "asin", Math.Asin }, { "acos", Math.Acos }, { "atan", Math.Atan },
            { "sinh", Math.Sinh }, { "cosh", Math.Cosh }, { "tanh", Math.Tanh },
            { "asinh", v => Math.Log(v + Math.Sqrt(v * v + 1)) }, { "acosh", v => Math.Log(v + Math.Sqrt(v * v - 1)) }, { "atanh", v => 0.5 * Math.Log((1 + v) / (1 - v)) },
            { "log", Math.Log }, { "ln", Math.Log }, { "log10", Math.Log10 }, { "log2", v => Math.Log(v, 2) },
            { "exp", Math.Exp }, { "sqrt", Math.Sqrt }, { "cbrt", v => v < 0 ? -Math.Pow(-v, 1.0 / 3) : Math.Pow(v, 1.0 / 3) }, { "abs", Math.Abs },
        };

        Val Call(string name, Val[] a)
        {
            Func<double, double> f1;
            if (Unary1.TryGetValue(name, out f1)) { Args(name, a, 1); return Val.Num(f1(a[0].Number(name))); }
            switch (name)
            {
                case "pow": Args(name, a, 2); return Val.Num(Math.Pow(a[0].Number(name), a[1].Number(name)));
                case "hypot": Args(name, a, 2); return Val.Num(Math.Sqrt(a[0].Number(name) * a[0].F + a[1].Number(name) * a[1].F));
                case "atan2": Args(name, a, 2); return Val.Num(Math.Atan2(a[0].Number(name), a[1].Number(name)));
                case "fmod": { Args(name, a, 2); double x = a[0].Number(name), y = a[1].Number(name); return Val.Num(x - y * Math.Truncate(x / y)); }
                case "remainder": Args(name, a, 2); return Val.Num(Math.IEEERemainder(a[0].Number(name), a[1].Number(name)));
                case "min": case "max": case "sum":
                    {
                        var nums = a.SelectMany(v => v.T == 'm' ? v.A : new[] { v }).Select(v => v.Number(name)).ToList();
                        if (nums.Count == 0) throw new ExprException(name + " needs arguments");
                        return Val.Num(name == "min" ? nums.Min() : name == "max" ? nums.Max() : nums.Sum());
                    }
                case "sizeof": Args(name, a, 1); return Val.Int(a[0].T == 'm' ? a[0].A.Length : 1);
                case "strlen": Args(name, a, 1); return Val.Int(Str(name, a[0]).Length);
                case "str2dbl": Args(name, a, 1); { double d; double.TryParse(Str(name, a[0]).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d); return Val.Num(d); }
                case "toupper": Args(name, a, 1); return Val.Str(Str(name, a[0]).ToUpperInvariant());
                case "tolower": Args(name, a, 1); return Val.Str(Str(name, a[0]).ToLowerInvariant());
                case "regexSearch":
                    {
                        Args(name, a, 2);
                        var m = new Regex(Str(name, a[0]), RegexOptions.ECMAScript).Match(Str(name, a[1]));
                        return Val.Arr(m.Success ? m.Groups.Cast<Group>().Select(g => Val.Str(g.Value)).ToArray() : new Val[0]);
                    }
                case "regexReplace":
                    Args(name, a, 3);
                    return Val.Str(new Regex(Str(name, a[0]), RegexOptions.ECMAScript).Replace(Str(name, a[1]), Str(name, a[2])));
                case "readRegString": case "readRegDWORD":
                    {
                        Args(name, a, 2);
                        UsedRegistry = true;
                        object v = ReadRegistry(Str(name, a[0]), Str(name, a[1]));
                        if (name == "readRegString")
                        {
                            if (!(v is string)) throw new ExprException("registry value is not text");
                            return Val.Str((string)v);
                        }
                        if (!(v is int)) throw new ExprException("registry value is not a DWORD");
                        return Val.Int((int)v);
                    }
            }
            throw new ExprException("unknown function " + name + "()");
        }

        static void Args(string name, Val[] a, int n) { if (a.Length != n) throw new ExprException(name + "() takes " + n + " argument" + (n == 1 ? "" : "s")); }
        static string Str(string name, Val v) { if (v.T != 's') throw new ExprException(name + "() needs text"); return v.S; }

        static object ReadRegistry(string key, string valueName)
        {
            if (!Native.OnWindows) throw new ExprException("the registry isn't available");
            int i = key.IndexOf('\\');
            string root = i < 0 ? key : key.Substring(0, i), sub = i < 0 ? "" : key.Substring(i + 1);
            Microsoft.Win32.RegistryKey baseKey;
            switch (root.ToUpperInvariant())
            {
                case "HKEY_LOCAL_MACHINE": case "HKLM": baseKey = Microsoft.Win32.Registry.LocalMachine; break;
                case "HKEY_CURRENT_USER": case "HKCU": baseKey = Microsoft.Win32.Registry.CurrentUser; break;
                case "HKEY_CLASSES_ROOT": case "HKCR": baseKey = Microsoft.Win32.Registry.ClassesRoot; break;
                case "HKEY_USERS": case "HKU": baseKey = Microsoft.Win32.Registry.Users; break;
                case "HKEY_CURRENT_CONFIG": case "HKCC": baseKey = Microsoft.Win32.Registry.CurrentConfig; break;
                default: throw new ExprException("unknown registry root " + root);
            }
            using (var k = baseKey.OpenSubKey(sub))
            {
                if (k == null) throw new ExprException("registry key not found");
                var v = k.GetValue(valueName);
                if (v == null) throw new ExprException("registry value not found");
                return v;
            }
        }
    }
}
