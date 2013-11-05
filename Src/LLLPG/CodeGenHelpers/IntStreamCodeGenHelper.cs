﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using Loyc;
using Loyc.Math;
using Loyc.Collections;
using Loyc.Syntax;
using S = Loyc.Syntax.CodeSymbols;

namespace Loyc.LLParserGenerator
{
	/// <summary>Standard code generator for character/integer input streams
	/// and is the default code generator for <see cref="LLParserGenerator"/>.</summary>
	public class IntStreamCodeGenHelper : CodeGenHelperBase
	{
		public const int EOF_int = PGIntSet.EOF_int;
		static readonly Symbol _EOF = GSymbol.Get("EOF");

		public override IPGTerminalSet EmptySet
		{
			get { return PGIntSet.Empty; }
		}

		public override Pred CodeToPred(LNode expr, ref string errorMsg)
		{
			bool isInt = false;
			PGIntSet set;

			if (expr.IsIdNamed(_underscore)) {
				set = PGIntSet.AllExceptEOF;
			} else if (expr.IsIdNamed(_EOF)) {
				set = PGIntSet.EOF;
			} else if (expr.Calls(S.DotDot, 2)) {
				int? from = ConstValue(expr.Args[0], ref isInt);
				int? to   = ConstValue(expr.Args[1], ref isInt);
				if (from == null || to == null) {
					errorMsg = "Expected int32 or character literal on each side of «..»";
					return null;
				}
				set = PGIntSet.WithRanges(from.Value, to.Value);
			} else if (expr.Value is string) {
				return Pred.Seq((string)expr.Value);
			} else {
				int? num = ConstValue(expr, ref isInt);
				if (num == null) {
					errorMsg = "Unrecognized expression; expected int32 or character literal";
					return null;
				}
				set = PGIntSet.With(num.Value);
			}
			set.IsCharSet = !isInt;
			return new TerminalPred(expr, set, true);
		}
		private int? ConstValue(LNode node, ref bool isInt)
		{
			node = ResolveAlias(node);
			
			object v = node.Value;
			if (v is char)
				return (char)v;
			else if (v is int) {
				isInt = true;
				return (int)v;
			} else
				return null;
		}

		public override IPGTerminalSet Optimize(IPGTerminalSet set, IPGTerminalSet dontcare)
		{
			return ((PGIntSet)set).Optimize((IntSet)dontcare);
		}

		public int? ExampleInt(PGIntSet set)
		{
			if (set.IsCharSet && set.IsInverted && set.Contains('_'))
				return '_';
			if (set.ContainsEverything)
				return 0;
			if (set.IsEmptySet)
				return null;

			int example = int.MinValue;
			int min = set.IsCharSet ? 32 : 0;
			foreach (var range in set.Runs()) {
				example = range.Lo < min ? range.Hi : range.Lo;
				if (example > min)
					break;
			}
			return example;
		}
		public override char? ExampleChar(IPGTerminalSet set_)
		{
			var set = ((PGIntSet)set_);
			
			if (!set.IsCharSet)
				return null;
			int? ex = ExampleInt(set);
			char c;
			if (ex == null || (c = (char)ex.Value) != ex.Value)
				return null;
			return c;
		}
		public override string Example(IPGTerminalSet set_)
		{
			var set = ((PGIntSet)set_);
			
			char? ch = ExampleChar(set);
			if (ch != null)
				return ch == '\'' ? @"'\''" : string.Format("'{0}'", ch);
			int? ex = ExampleInt(set);
			if (ex == null)
				return "<nothing>";
			if (ex == EOF_int)
				return "<EOF>";
			return ex.Value.ToString();
		}

		protected override LNode GenerateTest(IPGTerminalSet set, LNode subject, Symbol setName)
		{
			return ((PGIntSet)set).GenerateTest(subject, setName);
		}
		protected override LNode GenerateSetDecl(IPGTerminalSet set, Symbol setName)
		{
			return ((PGIntSet)set).GenerateSetDecl(setName);
		}

		public override LNode GenerateMatchExpr(IPGTerminalSet set_, bool savingResult, bool recognizerMode)
		{
			var set = (PGIntSet)set_;

			LNode call;
			if (set.Complexity(2, 3, !set.IsInverted) <= 6) {
				var args = new RWList<LNode>();
				if (set.Complexity(1, 2, true) > set.Count) {
					// Use MatchRange or MatchExceptRange
					foreach (var r in set) {
						if (!set.IsInverted || r.Lo != EOF_int || r.Hi != EOF_int) {
							args.Add((LNode)set.MakeLiteral(r.Lo));
							args.Add((LNode)set.MakeLiteral(r.Hi));
						}
					}
					var target = recognizerMode
						? (set.IsInverted ? _TryMatchExceptRange : _TryMatchRange)
						: (set.IsInverted ? _MatchExceptRange : _MatchRange);
					call = F.Call(target, args.ToRVList());
				} else {
					// Use Match or MatchExcept
					foreach (var r in set) {
						for (int c = r.Lo; c <= r.Hi; c++) {
							if (!set.IsInverted || c != EOF_int)
								args.Add((LNode)set.MakeLiteral(c));
						}
					}
					var target = recognizerMode
						? (set.IsInverted ? _TryMatchExcept : _TryMatch)
						: (set.IsInverted ? _MatchExcept : _Match);
					call = F.Call(target, args.ToRVList());
				}
			} else {
				var setName = GenerateSetDecl(set_);
				call = F.Call(recognizerMode ? _TryMatch : _Match, F.Id(setName));
			}
			return call;
		}

		public override LNode LAType()
		{
			return F.Int32;
		}

		protected override int GetRelativeCostForSwitch(IPGTerminalSet set)
		{
			var intset = (PGIntSet)set;
			int switchCost = (int)System.Math.Min(1 + intset.Size, 1000000);
			int ifCost = System.Math.Min(intset.Complexity(4, 8, true), 32);
			return ifCost - switchCost;
		}

		protected override IEnumerable<LNode> GetCases(IPGTerminalSet set)
		{
			var intset = (PGIntSet)set;
			foreach (IntRange range in intset) {
				for (int ch = range.Lo; ch <= range.Hi; ch++) {
					bool isChar = intset.IsCharSet && (char)ch == ch;
					yield return F.Literal(isChar ? (object)(char)ch : (object)ch);
				}
			}
		}
	}
}
