﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Loyc.Syntax;
using Loyc.Utilities;
using LEL.Prelude;
using Loyc.Collections;
using Loyc.Syntax.Lexing;
using S = Loyc.Syntax.CodeSymbols;
using Loyc.Math;

namespace Loyc.LLParserGenerator
{
	public static class LelIntegration
	{
		static readonly Symbol _rule = GSymbol.Get("rule");
		static readonly Symbol _term = GSymbol.Get("term");
		static readonly Symbol _def = GSymbol.Get("def");
		static readonly Symbol _lexer = GSymbol.Get("lexer");
		static readonly Symbol _parser = GSymbol.Get("parser");
		static readonly LNodeFactory F = new LNodeFactory(new EmptySourceFile("LLLPG"));

		/// <summary>Helper macro that translates <c>lexer</c> in <c>LLLPG(lexer, {...})</c> 
		/// into a <see cref="IntStreamCodeGenHelper"/> object.</summary>
		[SimpleMacro("LLLPG")]
		public static LNode LLLPG_lexer(LNode node, IMessageSink sink)
		{
			LNode helper;
			if (node.ArgCount != 2 || (helper = node.Args[0]).Name != _lexer)
				return null;
			if (helper.ArgCount != 0) {
				sink.Write(MessageSink.Error, helper, "lexer: no arguments expected");
				return null;
			}
			return node.WithArgChanged(0, F.Literal(new IntStreamCodeGenHelper()));
		}

		/// <summary>Helper macro that translates <c>parser</c> in <c>LLLPG(parser, {...})</c> 
		/// into a <see cref="GeneralCodeGenHelper"/> object.</summary>
		[SimpleMacro("LLLPG")]
		public static LNode LLLPG_parser(LNode node, IMessageSink sink)
		{
			LNode helper;
			if (node.ArgCount != 2 || (helper = node.Args[0]).Name != _parser)
				return null;
			var arg0 = helper.Args.TryGet(0, null) ?? F.Literal("#int32");
			var arg1 = helper.Args.TryGet(1, null) ?? F.Literal(true);
			if (!helper.ArgCount.IsInRange(0, 2) || !(arg0.Value is string) || !(arg1.Value is bool)) {
				sink.Write(MessageSink.Error, helper, "parser: expected arguments (laType::string, allowSwitch::bool). The arguments must be literals.");
				return null;
			}
			return node.WithArgChanged(0, F.Literal(new GeneralCodeGenHelper((string)arg0.Value, (bool)arg1.Value)));
		}

		[SimpleMacro]
		public static LNode LLLPG(LNode node, IMessageSink sink)
		{
			IPGCodeGenHelper helper = null;
			if (!node.ArgCount.IsInRange(1, 2) || !node.Args.Last.Calls(S.Braces) 
				|| (node.ArgCount == 2 && null == (helper = node.Args[0].Value as IPGCodeGenHelper))) {
				sink.Write(MessageSink.Error, node, "Expected LLLPG({...}), which means LLLPG(parser(), {...}), or LLLPG(lexer, {...})");
				return null;
			}

			helper = helper ?? new GeneralCodeGenHelper();
			var lllpg = new LLParserGenerator(helper);
			var rules = new Dictionary<Symbol,Pair<Rule, TokenTree>>();

			// Read option attributes, if any
			for (int i = 0; i < node.Attrs.Count; i++) {
				var attr = node.Attrs[i];
				switch (attr.Name.Name) {
					case "FullLLk":
						ReadOption<bool>(sink, lllpg, attr, v => lllpg.FullLLk = v, true);
						break;
					case "Verbosity":
						ReadOption<int>(sink, lllpg, attr, v => lllpg.Verbosity = v, null);
						break;
					case "NoDefaultArm":
						ReadOption<bool>(sink, lllpg, attr, v => lllpg.NoDefaultArm = v, null);
						break;
					case "DefaultK":
						ReadOption<int>(sink, lllpg, attr, v => lllpg.DefaultK = v, null);
						break;
					default:
						sink.Write(MessageSink.Error, attr,
							"Unrecognized attribute. LLLPG supports the following options: "+
							"FullLLk(bool), Verbosity(0..3), NoDefaultArm(bool), and DefaultK(1..9)");
						break;
				}
			}

			// Gather up the rule definitions, create Rule objects
			foreach (var rule in node.Args)
			{
				if (rule.Calls(_rule)) {
					// Create a method body to use for the rule
					TokenTree ruleBody;
					if (rule.ArgCount != 2 || null == (ruleBody = rule.Args[1].Value as TokenTree)) {
						sink.Write(MessageSink.Error, rule, "A rule should have the form rule(Name(Args)::ReturnType, @[...])");
					} else {
						var basis = LEL.Prelude.Macros.def(
							rule.With(_def, new RVList<LNode>(rule.Args[0])), sink);
						if (basis != null) {
							if (basis.Args[1].IsCall)
								sink.Write(MessageSink.Error, basis.Args[1], "A rule must have a simple name");
							else {
								var name = basis.Args[1].Name;
								if (rules.ContainsKey(name))
									sink.Write(MessageSink.Error, name, "This rule name was used before at {0}", rules[name].A.Basis.Range.Begin);
								else
									rules.Add(name, Pair.Create(new Rule(basis, name, null, true), ruleBody));
							}
						}
					}
				}
			}

			// Parse the rule definitions (knowing the name of all the rules, 
			// we can decide if an Id refers to a rule; if not, it's assumed
			// to refer to a terminal).
			throw new NotImplementedException();
			// LLLPG predicates:
			// 'x'       character
			// 1, @@foo  terminal
			// id.id     terminal
			// 'a'..'z'  set
			// 1..10     set
			// ~a ~(a|b) set
			// id        rule OR terminal - gather rule names before parsing?
			// id(args)  rule invocation (need host language parser to parse args!)
			// a b       sequence
			// a|b a/b   alts
			// a* a? a+  alts

			// Process the grammar & generate code
			
			// Replace each rule with its generated code.
		}

		private static void ReadOption<T>(IMessageSink sink, LLParserGenerator lllpg, LNode attr, Action<T> setter, T? defaultValue) where T:struct
		{
			if (attr.ArgCount > 1 || (attr.ArgCount == 0 && defaultValue == null))
				sink.Write(MessageSink.Error, attr, Localize.From("{0}: one parameter expected", Signature(attr, typeof(T), defaultValue)));
			else if (attr.ArgCount == 1) {
				if (attr.Args[0].Value is T)
					setter((T)attr.Args[0].Value);
				else
					sink.Write(MessageSink.Error, attr, Localize.From("{0}: literal of type «{1}» expected", Signature(attr, typeof(T), defaultValue), typeof(T).Name));
			} else
				setter(defaultValue.Value);
		}
		private static string Signature(LNode attr, Type type, object defaultValue)
		{
			return string.Format(defaultValue == null ? "{0}({1})" : "{0}({1} = {2})",
				attr.Name, type.Name, defaultValue);
		}
	}
}