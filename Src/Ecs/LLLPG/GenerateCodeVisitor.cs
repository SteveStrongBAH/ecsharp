﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Loyc.CompilerCore;
using Loyc.Collections;
using Loyc.Collections.Impl;

namespace Loyc.LLParserGenerator
{
	using S = ecs.CodeSymbols;
	using System.Diagnostics;
	using Loyc.Utilities;

	partial class LLParserGenerator
	{
		protected class GenerateCodeVisitor : PredVisitor
		{
			public LLParserGenerator LLPG;
			public GreenFactory F;
			Node _backupBasis;
			Rule _currentRule;
			Pred _currentPred;
			Node _classBody; // Location where we generate terminal sets
			Node _target; // Location where we're currently generating code
			ulong _laVarsNeeded;
			// # of alts using gotos -- a counter is used to make unique labels
			int _separatedMatchCounter = 0;
			int _k;

			public GenerateCodeVisitor(LLParserGenerator llpg, GreenFactory f, Node classBody)
			{
				LLPG = llpg;
				F = f;
				_classBody = classBody;
				_backupBasis = CompilerCore.Node.NewSynthetic(GSymbol.Get("nowhere"), new SourceRange(F.File, -1, -1));
			}

			public static readonly Symbol _alt = GSymbol.Get("alt");

			public void Generate(Rule rule)
			{
				_currentRule = rule;
				_k = rule.K > 0 ? rule.K : LLPG.DefaultK;
				//   ruleMethod = @{ public void \(F.Symbol(rule.Name))() { } }
				Node ruleMethod = Node.FromGreen(F.Attr(F.Public, F.Def(F.Void, F.Symbol(rule.Name), F.List(), F.Braces())));
				Node body = _target = ruleMethod.Args[3];
				_laVarsNeeded = 0;
				_separatedMatchCounter = 0;
				LLPG._setNameCounter = 0;
				
				Visit(rule.Pred);

				if (_laVarsNeeded != 0) {
					Node laVars = Node.FromGreen(F.Call(S.Var, LLPG.LAType()));
					for (int i = 0; _laVarsNeeded != 0; i++, _laVarsNeeded >>= 1)
						if ((_laVarsNeeded & 1) != 0)
							laVars.Args.Add(Node.NewSynthetic(GSymbol.Get("la" + i.ToString()), F.File));
					body.Args.Insert(0, laVars);
				}
				_classBody.Args.Add(ruleMethod);
			}

			new public void Visit(Pred pred)
			{
				if (pred.PreAction != null)
					_target.Args.AddSpliceClone(pred.PreAction);
				_currentPred = pred;
				pred.Call(this);
				if (pred.PostAction != null)
					_target.Args.AddSpliceClone(pred.PostAction);
			}

			void VisitWithNewTarget(Pred toBeVisited, Node target)
			{
				Node old = _target;
				_target = target;
				Visit(toBeVisited);
				_target = old;
			}

			// Visit(Alts) is the most important method. It generates all prediction code,
			// which is the majority of the code in a parser.
			public override void Visit(Alts alts)
			{
				var firstSets = LLPG.ComputeFirstSets(alts);
				var timesUsed = new Dictionary<int, int>();
				PredictionTree tree = ComputePredictionTree(firstSets, timesUsed);

				SimplifyPredictionTree(tree);

				GenerateCodeForAlts(alts, timesUsed, tree);
			}

			#region PredictionTree class and ComputePredictionTree()

			/// <summary>A <see cref="PredictionTree"/> or a single alternative to assume.</summary>
			protected struct PredictionTreeOrAlt
			{
				public static implicit operator PredictionTreeOrAlt(PredictionTree t) { return new PredictionTreeOrAlt { Tree = t }; }
				public static implicit operator PredictionTreeOrAlt(int alt) { return new PredictionTreeOrAlt { Alt = alt }; }
				public PredictionTree Tree;
				public int Alt; // used if Tree==null

				public override string ToString()
				{
					return Tree != null ? Tree.ToString() : string.Format("alt #{0}", Alt);
				}
			}

			/// <summary>An abstract representation of a prediction tree, which 
			/// will be transformed into prediction code. PredictionTree has a list
			/// of <see cref="PredictionBranch"/>es at a particular level of lookahead.
			/// </summary><remarks>
			/// This represents the final result of lookahead analysis, in contrast 
			/// to the <see cref="KthSet"/> class which is lower-level and 
			/// represents specific transitions in the grammar. A single 
			/// branch in a prediction tree could be derived from a single case 
			/// in a KthSet, or it could represent several different cases from
			/// one or more different KthSets.
			/// </remarks>
			protected class PredictionTree
			{
				public PredictionTree(int la, InternalList<PredictionBranch> children, IPGTerminalSet coverage)
				{
					Lookahead = la;
					Children = children;
					TotalCoverage = coverage;
				}
				public InternalList<PredictionBranch> Children = InternalList<PredictionBranch>.Empty;
				// only used if Children is empty. Alt=0 for first alternative, -1 for exit
				public IPGTerminalSet TotalCoverage; // null for an assertion level
				public int Lookahead; // starts at 0 for first terminal of lookahead

				public bool IsAssertionLevel { get { return TotalCoverage == null; } }

				public override string ToString()
				{
					var s = new StringBuilder(
						string.Format(IsAssertionLevel ? "test and-predicates at LA({0}):" : "test LA({0}):", Lookahead));
					for (int i = 0; i < Children.Count; i++) {
						s.Append("\n  ");
						s.Append(Children[i].ToString().Replace("\n", "\n  "));
					}
					return s.ToString();
				}
			}

			/// <summary>Represents one branch (if statement or case) in a prediction tree.</summary>
			/// <remarks>
			/// For example, code like 
			/// <code>if (la0 == 'a' || la0 == 'A') { code for first alternative }</code>
			/// is represented by a PredictionBranch with <c>Set = [aA]</c> and <c>Alt = 0.</c>
			/// </remarks>
			protected class PredictionBranch
			{
				public PredictionBranch(Set<AndPred> andPreds, PredictionTreeOrAlt sub)
				{
					AndPreds = andPreds;
					Sub = sub;
				}
				public PredictionBranch(IPGTerminalSet set, PredictionTreeOrAlt sub, IPGTerminalSet covered)
				{
					Set = set;
					Sub = sub;
					Covered = covered;
				}

				public IPGTerminalSet Set;    // used in standard prediction levels
				public Set<AndPred> AndPreds; // used in assertion levels

				public PredictionTreeOrAlt Sub;
				
				public IPGTerminalSet Covered;

				public override string ToString() // for debugging
				{
					return string.Format("when {0} {1}, {2}", 
						StringExt.Join("", AndPreds), Set, Sub.ToString());
				}
			}
			
			protected PredictionTree ComputePredictionTree(KthSet[] kthSets, Dictionary<int, int> timesUsed)
			{
				var children = InternalList<PredictionBranch>.Empty;
				var thisBranch = new List<KthSet>();
				int lookahead = kthSets[0].LA;
				Debug.Assert(kthSets.All(p => p.LA == lookahead));

				IPGTerminalSet covered = TrivialTerminalSet.Empty();
				for (;;)
				{
					thisBranch.Clear();
					// e.g. given an Alts value of ('0' '0'..'7'+ | '0'..'9'+), 
					// ComputeSetForNextBranch finds the set '0' in the first 
					// iteration (recording both alts in 'thisBranch'), '1'..'9' 
					// on the second iteration, and finally null.
					IPGTerminalSet set = ComputeSetForNextBranch(kthSets, thisBranch, covered);

					if (set == null)
						break;

					if (thisBranch.Count == 1) {
						var branch = thisBranch[0];
						children.Add(new PredictionBranch(branch.Set, branch.Alt, covered));
						CountAlt(branch.Alt, timesUsed);
					} else {
						Debug.Assert(thisBranch.Count > 1);
						NarrowDownToSet(thisBranch, set);

						PredictionTreeOrAlt sub;
						if (thisBranch.Any(ks => ks.HasAnyAndPreds))
							sub = ComputeAssertionTree(thisBranch, timesUsed);
						else
							sub = ComputeNestedPredictionTree(thisBranch, timesUsed);
						children.Add(new PredictionBranch(set, sub, covered));
					}

					covered = covered.Union(set) ?? set.Union(covered);
				}
				return new PredictionTree(lookahead, children, covered);
			}

			private void CountAlt(int alt, Dictionary<int, int> timesUsed)
			{
				int counter;
				timesUsed.TryGetValue(alt, out counter);
				timesUsed[alt] = counter + 1;
			}

			private PredictionTreeOrAlt ComputeNestedPredictionTree(List<KthSet> prevSets, Dictionary<int, int> timesUsed)
			{
				Debug.Assert(prevSets.Count > 0);
				int lookahead = prevSets[0].LA;
				if (prevSets.Count == 1 || lookahead + 1 >= _k)
				{
					if (prevSets.Count > 1 && ShouldReportAmbiguity(prevSets))
					{
						LLPG.Output(_currentPred.Basis, _currentPred, Warning,
							string.Format("Alternatives ({0}) are ambiguous for input such as {1}",
								StringExt.Join(", ", prevSets.Select(
									ks => ks.Alt == -1 ? "exit" : (ks.Alt + 1).ToString())),
								GetAmbiguousCase(prevSets)));
					}
					var @default = prevSets[0];
					CountAlt(@default.Alt, timesUsed);
					return (PredictionTreeOrAlt) @default.Alt;
				}
				KthSet[] nextSets = LLPG.ComputeNextSets(prevSets);
				var subtree = ComputePredictionTree(nextSets, timesUsed);
				
				return subtree;
			}

			private bool ShouldReportAmbiguity(List<KthSet> prevSets)
			{
				// Look for any and-predicates that are unique to particular 
				// branches. Such predicates can suppress warnings.
				var andPreds = new List<Set<AndPred>>();
				var common = new Set<AndPred>();
				bool first = true;
				foreach (var ks in prevSets) {
					var andSet = new Set<AndPred>();
					for (var ks2 = ks; ks2 != null; ks2 = ks2.Prev)
						andSet = andSet | ks2.AndReq;
					andPreds.Add(andSet);
					common = first ? andSet : andSet & common;
					first = false;
				}
				ulong suppressWarnings = 0;
				for (int i = 0; i < andPreds.Count; i++) {
					if (!(andPreds[i] - common).IsEmpty)
						suppressWarnings |= 1ul << i;
				}

				return ((Alts)_currentPred).ShouldReportAmbiguity(prevSets.Select(ks => ks.Alt), suppressWarnings);
			}

			private string GetAmbiguousCase(List<KthSet> lastSets)
			{
				var seq = new List<IPGTerminalSet>();
				IEnumerable<KthSet> kthSets = lastSets;
				for(;;) {
					IPGTerminalSet tokSet = null;
					foreach(KthSet ks in kthSets)
						tokSet = tokSet == null ? ks.Set : (tokSet.Intersection(ks.Set) ?? ks.Set.Intersection(tokSet));
					if (tokSet == null)
						break;
					seq.Add(tokSet);
					Debug.Assert(!kthSets.Any(ks => ks.Prev == null));
					kthSets = kthSets.Select(ks => ks.Prev);
				}
				seq.Reverse();
				
				var result = new StringBuilder("«");
				if (seq.All(set => set.ExampleChar != null)) {
					StringBuilder temp = new StringBuilder();
					foreach(var set in seq)
						temp.Append(set.ExampleChar);
					result.Append(G.EscapeCStyle(temp.ToString(), EscapeC.Control, '»'));
				} else {
					result.Append(seq.Select(set => set.Example).Join(" "));
				}
				result.Append("» (");
				result.Append(seq.Join(", "));
				result.Append(')');
				return result.ToString();
			}

			private void NarrowDownToSet(List<KthSet> thisBranch, IPGTerminalSet set)
			{
				// Scans the Transitions of thisBranch, removing cases that are
				// unreachable given the current set and intersecting the reachable 
				// sets with 'set'. This method is needed in rare cases involving 
				// nested Alts, but it is called unconditionally just in case 
				// futher lookahead steps might rely on the results. Here are two
				// examples where it is needed:
				//
				// ( ( &foo 'a' | 'b' 'b') | 'b' 'c' )
				//
				// In this case, a prediction subtree is generated for LA(0)=='b'.
				// Initially, thisBranch will contain a case for (&foo 'a') but it
				// is unreachable given that we know LA(0)=='b', so &foo should not 
				// be tested. This method will remove that case so it'll be ignored.
				//
				// (('a' | 'd' 'd') 't' | ('a'|'o') 'd' 'd') // test suite: NestedAlts()
				// 
				// Without this method, prediction would think that the sequence 
				// 'a' 'd' could match the first alt because it fails to discard the
				// second nested alt ('d' 'd') after matching 'a'.
				//
				// LL(k) prediction still doesn't work perfectly in all cases. For
				// example, this case is predicted incorrectly:
				// 
				// ( ('a' 'b' | 'b' 'a') 'c' | ('b' 'b' | 'a' 'a') 'c' )
				for (int i = 0; i < thisBranch.Count; i++)
					thisBranch[i] = NarrowDownToSet(thisBranch[i], set);
			}
			private KthSet NarrowDownToSet(KthSet kthSet, IPGTerminalSet set)
			{
				kthSet = kthSet.Clone();
				var cases = kthSet.Cases;
				for (int i = cases.Count-1; i >= 0; i--)
				{
					cases[i].Set = cases[i].Set.Intersection(set);
					if (cases[i].Set.IsEmptySet)
						cases.RemoveAt(i);
				}
				kthSet.UpdateSet(kthSet.Set.ContainsEOF);
				Debug.Assert(cases.Count > 0);
				return kthSet;
			}

			private static IPGTerminalSet ComputeSetForNextBranch(KthSet[] kthSets, List<KthSet> thisBranch, IPGTerminalSet covered)
			{
				int i;
				IPGTerminalSet set = null;
				for (i = 0; ; i++)
				{
					if (i == kthSets.Length)
						return null; // done!
					set = kthSets[i].Set.Subtract(covered) ?? covered.Intersection(kthSets[i].Set, false, true);
					if (!set.IsEmptySet)
						break;
				}

				thisBranch.Add(kthSets[i]);
				for (i++; i < kthSets.Length; i++)
				{
					var next = set.Intersection(kthSets[i].Set) ?? kthSets[i].Set.Intersection(set);
					if (!next.IsEmptySet)
					{
						set = next;
						thisBranch.Add(kthSets[i]);
					}
				}

				return set;
			}

			private PredictionTreeOrAlt ComputeAssertionTree(List<KthSet> alts, Dictionary<int, int> timesUsed)
			{
				var children = InternalList<PredictionBranch>.Empty;

				// If any AndPreds show up in all cases, they are irrelevant for
				// prediction and should be ignored.
				var commonToAll = alts.Aggregate(null, (HashSet<AndPred> set, KthSet alt) => {
					if (set == null) return alt.AndReq.ClonedHashSet();
					set.IntersectWith(alt.AndReq.InternalSet);
					return set;
				});
				return ComputeAssertionTree2(alts, new Set<AndPred>(commonToAll), timesUsed);
			}
			private PredictionTreeOrAlt ComputeAssertionTree2(List<KthSet> alts, Set<AndPred> matched, Dictionary<int, int> timesUsed)
			{
				int lookahead = alts[0].LA;
				var children = InternalList<PredictionBranch>.Empty;
				HashSet<AndPred> falsified = new HashSet<AndPred>();
				// Each KthSet represents a branch of the Alts for which we are 
				// generating a prediction tree; so if we find an and-predicate 
				// that, by failing, will exclude one or more KthSets, that's
				// probably the fastest way to get closer to completing the tree.
				// Any predicate in KthSet.AndReq (that isn't in matched) satisfies
				// this condition.
				var bestAndPreds = alts.SelectMany(alt => alt.AndReq).Where(ap => !matched.Contains(ap)).ToList();
				foreach (AndPred andPred in bestAndPreds)
				{
					if (!falsified.Contains(andPred))
						children.Add(MakeBranchForAndPred(andPred, alts, matched, timesUsed, falsified));
				}
				// Testing any single AndPred will not exclude any KthSets, so
				// we'll proceed the slow way: pick any unmatched AndPred and test 
				// it. If it fails then the Transition(s) associated with it can be 
				// excluded.
				foreach (Transition trans in
					alts.SelectMany(alt => alt.Cases)
						.Where(trans => !matched.Overlaps(trans.AndPreds) && !falsified.Overlaps(trans.AndPreds)))
					foreach(var andPred in trans.AndPreds)
						children.Add(MakeBranchForAndPred(andPred, alts, matched, timesUsed, falsified));

				if (children.Count == 0)
				{
					// If no AndPreds were tested, proceed to the next level of prediction.
					Debug.Assert(falsified.Count == 0);
					return ComputeNestedPredictionTree(alts, timesUsed);
				}
				
				// If there are any "unguarded" cases left after falsifying all 
				// the AndPreds, add a branch for them.
				Debug.Assert(falsified.Count > 0);
				alts = RemoveFalsifiedCases(alts, falsified);
				if (alts.Count > 0)
				{
					var final = new PredictionBranch(new Set<AndPred>(), ComputeNestedPredictionTree(alts, timesUsed));
					children.Add(final);
				}
				return new PredictionTree(lookahead, children, null);
			}
			private PredictionBranch MakeBranchForAndPred(AndPred andPred, List<KthSet> alts, Set<AndPred> matched, Dictionary<int, int> timesUsed, HashSet<AndPred> falsified)
			{
				if (falsified.Count > 0)
					alts = RemoveFalsifiedCases(alts, falsified);

				var apSet = GetBuddies(alts, andPred);
				Debug.Assert(!apSet.IsEmpty);
				var innerMatched = matched | apSet;
				var result = new PredictionBranch(apSet, ComputeAssertionTree2(alts, innerMatched, timesUsed));
				falsified.UnionWith(apSet);
				return result;
			}
			private List<KthSet> RemoveFalsifiedCases(List<KthSet> alts, HashSet<AndPred> falsified)
			{
				var results = new List<KthSet>(alts.Count);
				for (int i = 0; i < alts.Count; i++) {
					KthSet alt = alts[i].Clone();
					for (int c = alt.Cases.Count - 1; c >= 0; c--)
						if (falsified.Overlaps(alt.Cases[c].AndPreds))
							alt.Cases.RemoveAt(c);
					if (alt.Cases.Count > 0)
						results.Add(alt);
				}
				return results;
			}
			private Set<AndPred> GetBuddies(List<KthSet> alts, AndPred ap)
			{
				// Given an AndPred, find any other AndPreds that always appear 
				// together with ap; if any are found, we want to group them 
				// together because doing so will simplify the prediction tree.
				return new Set<AndPred>(
					alts.SelectMany(alt => alt.Cases)
						.Where(trans => trans.AndPreds.Contains(ap))
						.Aggregate(null, (HashSet<AndPred> set, Transition trans) => {
							if (set == null) return new HashSet<AndPred>(trans.AndPreds);
							set.IntersectWith(trans.AndPreds);
							return set;
						}));
			}
			
			#endregion

			/// <summary>Recursively merges adjacent duplicate cases in prediction trees.
			/// The tree is modified in-place, but in case a tree collapses to a single 
			/// alternative, the return value indicates which single alternative.</summary>
			private PredictionTreeOrAlt SimplifyPredictionTree(PredictionTree tree)
			{
				for (int i = 0; i < tree.Children.Count; i++) {
					PredictionBranch pb = tree.Children[i];
					if (pb.Sub.Tree != null)
						pb.Sub = SimplifyPredictionTree(pb.Sub.Tree);
				}
				for (int i = tree.Children.Count-1; i > 0; i--) {
					PredictionBranch a = tree.Children[i-1], b = tree.Children[i];
					if (a.Sub.Tree == null && b.Sub.Tree == null &&
						a.Sub.Alt == b.Sub.Alt &&
						a.AndPreds.SetEquals(b.AndPreds))
					{
						// Merge a and b
						if (a.Set != null)
							a.Set = a.Set.Union(b.Set) ?? b.Set.Union(a.Set);
						tree.Children.RemoveAt(i);
					}
				}
				if (tree.Children.Count == 1)
					return tree.Children[0].Sub;
				return tree;
			}

			#region GenerateCodeForAlts() and related: generates code based on a prediction tree

			// GENERATED CODE EXAMPLE: The methods in this region generate
			// the for(;;) loop in this example and everything inside it, except
			// the calls to Match() which are generated by Visit(TerminalPred).
			// The generated code uses "goto" and "match" blocks in some cases
			// to avoid code duplication. This occurs when the matching code 
			// requires multiple statements AND appears more than once in the 
			// prediction tree. Otherwise, matching is done "inline" during 
			// prediction. We generate a for(;;) loop for (...)*, and in certain 
			// cases, we generates a do...while(false) loop for (...)?.
			//
			// rule Foo ==> #[ (('a'|'A') 'A')* 'a'..'z' 'a'..'z' ];
			// public void Foo()
			// {
			//     int la0, la1;
			//     for (;;) {
			//         la0 = LA(0);
			//         if (la0 == 'a') {
			//             la1 = LA(1);
			//             if (la1 == 'A')
			//                 goto match1;
			//             else
			//                 break;
			//         } else if (la0 == 'A')
			//             goto match1;
			//         else
			//             break;
			//         match1:
			//         {
			//             Match('A', 'a');
			//             Match('A');
			//         }
			//     }
			//     MatchRange('a', 'z');
			//     MatchRange('a', 'z');
			// }

			private void GenerateCodeForAlts(Alts alts, Dictionary<int, int> timesUsed, PredictionTree tree)
			{
				// Generate matching code for each arm
				Pair<Node, bool>[] matchingCode = new Pair<Node, bool>[alts.Arms.Count];
				HashSet<int> unreachable = new HashSet<int>();
				int separateCount = 0;
				for (int i = 0; i < alts.Arms.Count; i++)
				{
					if (!timesUsed.ContainsKey(i)) {
						unreachable.Add(i+1);
						continue;
					}

					var braces = Node.NewSynthetic(S.Braces, F.File);
					VisitWithNewTarget(alts.Arms[i], braces);

					matchingCode[i].A = braces;
					int stmts = braces.ArgCount;
					if (matchingCode[i].B = timesUsed[i] > 1 && (stmts > 1 || (stmts == 1 && braces.Args[0].Name == S.If)))
						separateCount++;
				}

				if (unreachable.Count == 1)
					LLPG.Output(alts.Basis, alts, Warning, string.Format("Branch {0} is unreachable.", unreachable.First()));
				else if (unreachable.Count > 1)
					LLPG.Output(alts.Basis, alts, Warning, string.Format("Branches {0} are unreachable.", unreachable.Join(", ")));
				if (!timesUsed.ContainsKey(-1) && alts.Mode != LoopMode.None)
					LLPG.Output(alts.Basis, alts, Warning, "Infinite loop. The exit branch is unreachable.");

				Symbol haveLoop = null;

				// Generate a loop body for (...)* or (...)?:
				var target = _target;
				if (alts.Mode == LoopMode.Star)
				{
					// (...)* => for (;;) {}
					var loop = Node.FromGreen(F.Call(S.For, new GreenAtOffs[] { F._Missing, F._Missing, F._Missing, F.Braces() }));
					_target.Args.Add(loop);
					target = loop.Args[3];
					haveLoop = S.For;
				}
				else if (alts.Mode == LoopMode.Opt && (uint)alts.DefaultArm < (uint)alts.Arms.Count)
					haveLoop = S.Do;

				// If the code for an arm is nontrivial and appears multiple times 
				// in the prediction table, it will have to be split out into a 
				// labeled block and reached via "goto". I'd rather just do a goto
				// from inside one "if" statement to inside another, but in C# 
				// (unlike in C and unlike in CIL) that is prohibited :(
				Node extraMatching = GenerateExtraMatchingCode(matchingCode, separateCount, ref haveLoop);

				Node code = GeneratePredictionTreeCode(tree, matchingCode, haveLoop);

				if (haveLoop == S.Do)
				{
					// (...)? => do {} while(false); IF the exit branch is NOT the default.
					// If the exit branch is the default, then no loop and no "break" is needed.
					var loop = Node.FromGreen(F.Call(S.Do, F.Braces(), F.@false));
					_target.Args.Add(loop);
					target = loop.Args[0];
				}
				
				if (code.Calls(S.Braces)) {
					while (code.ArgCount != 0)
						target.Args.Add(code.TryGetArg(0).Detach());
				} else
					target.Args.Add(code);

				if (extraMatching != null)
					while (extraMatching.ArgCount != 0)
						target.Args.Add(extraMatching.TryGetArg(0).Detach());
			}

			private Node GenerateExtraMatchingCode(Pair<Node, bool>[] matchingCode, int separateCount, ref Symbol needLoop)
			{
				Node extraMatching = null;
				if (separateCount != 0)
				{
					int labelCounter = 0;
					int skipCount = 0;
					Node firstSkip = null;
					string suffix = NextGotoSuffix();

					extraMatching = Node.NewSynthetic(S.Braces, F.File);
					for (int i = 0; i < matchingCode.Length; i++)
					{
						if (matchingCode[i].B) // split out this case
						{
							var label = F.Symbol("match" + (++labelCounter) + suffix);

							// break/continue; matchN: matchingCode[i].A;
							var skip = Node.FromGreen(F.Call(needLoop == S.For ? S.Continue : S.Break));
							firstSkip = firstSkip ?? skip;
							extraMatching.Args.Add(skip);
							extraMatching.Args.Add(Node.FromGreen(F.Call(S.Label, label)));
							extraMatching.Args.Add(matchingCode[i].A);
							skipCount++;

							// put @@{ goto matchN; } in prediction tree
							matchingCode[i].A = Node.FromGreen(F.Call(S.Goto, label));
						}
					}
					Debug.Assert(firstSkip != null);
					if (separateCount == matchingCode.Length)
					{
						// All of the matching code was split out, so the first 
						// break/continue statement is not needed.
						firstSkip.Detach();
						skipCount--;
					}
					if (skipCount > 0 && needLoop == null)
						// add do...while(false) loop so that the break statements make sense
						needLoop = S.Do; 
				}
				return extraMatching;
			}

			private string NextGotoSuffix()
			{
				if (_separatedMatchCounter == 0)
					return "";
				if (_separatedMatchCounter++ > 26)
					return string.Format("_{0}", _separatedMatchCounter - 1);
				else
					return ((char)('a' + _separatedMatchCounter - 1)).ToString();
			}

			protected Node GetPredictionSubtree(PredictionBranch branch, Pair<Node, bool>[] matchingCode, Symbol haveLoop)
			{
				if (branch.Sub.Tree != null)
					return GeneratePredictionTreeCode(branch.Sub.Tree, matchingCode, haveLoop);
				else {
					if (branch.Sub.Alt == -1)
						return Node.FromGreen(haveLoop != null ? F.Call(S.Break) : F.Symbol(S.Missing));
					else {
						var code = matchingCode[branch.Sub.Alt].A;
						if (code.Calls(S.Braces, 1))
							return code.Args[0].Clone();
						else
							return code.Clone();
					}
				}
			}

			protected Node GeneratePredictionTreeCode(PredictionTree tree, Pair<Node,bool>[] matchingCode, Symbol haveLoop)
			{
				var braces = Node.NewSynthetic(S.Braces, F.File);

				Debug.Assert(tree.Children.Count >= 1);
				int i = tree.Children.Count;
				bool needErrorBranch = LLPG.NoDefaultArm && (tree.IsAssertionLevel 
					? !tree.Children[i-1].AndPreds.IsEmpty
					: !tree.TotalCoverage.ContainsEverything);

				if (!needErrorBranch && tree.Children.Count == 1)
					return GetPredictionSubtree(tree.Children[0], matchingCode, haveLoop);

				Node block;
				GreenNode laVar = null;
				if (tree.IsAssertionLevel) {
					block = Node.NewSynthetic(S.Braces, F.File);
					block.IsCall = true;
				} else {
					_laVarsNeeded |= 1ul << tree.Lookahead;
					laVar = F.Symbol("la" + tree.Lookahead.ToString());
					// block = @@{{ \laVar = \(LA(context.Count)); }}
					block = Node.FromGreen(F.Braces(F.Call(S.Set, laVar, LLPG.LA(tree.Lookahead))));
				}

				// From the prediction table, generate a chain of if-else 
				// statements in reverse, starting with the final "else" clause
				Node @else;
				if (needErrorBranch)
					@else = LLPG.ErrorBranch(_currentRule, tree.TotalCoverage);
				else
					@else = GetPredictionSubtree(tree.Children[--i], matchingCode, haveLoop);
				for (--i; i >= 0; i--)
				{
					var branch = tree.Children[i];
					Node test;
					if (tree.IsAssertionLevel)
						test = GenerateTest(branch.AndPreds);
					else {
						var set = branch.Set.Optimize(branch.Covered);
						test = GenerateTest(set, laVar);
					}

					Node @if = Node.NewSynthetic(S.If, F.File);
					@if.Args.Add(test);
					@if.Args.Add(GetPredictionSubtree(branch, matchingCode, haveLoop));
					if (!@else.IsSimpleSymbolWithoutPAttrs(S.Missing))
						@if.Args.Add(@else);
					@else = @if;
				}
				block.Args.Add(@else);
				return block;
			}

			private Node GenerateTest(Set<AndPred> andPreds)
			{
				Node test;
				test = null;
				foreach (AndPred ap in andPreds)
				{
					var next = LLPG.GenerateAndPredCheck(_classBody, _currentRule, ap, true);
					if (test == null)
						test = next;
					else {
						Node and = Node.NewSynthetic(S.And, F.File);
						and.Args.Add(test);
						and.Args.Add(next);
						test = and;
					}
				}
				return test;
			}
			private Node GenerateTest(IPGTerminalSet set, GreenNode laVar)
			{
				var laVar_ = Node.FromGreen(laVar);
				Node test = set.GenerateTest(laVar_, null);
				if (test == null)
				{
					var setName = LLPG.GenerateSetDecl(_classBody, _currentRule, set);
					test = set.GenerateTest(laVar_, setName);
				}
				return test;
			}

			#endregion

			public override void Visit(Seq pred)
			{
				foreach (var p in pred.List)
					Visit(p);
			}
			public override void Visit(Gate pred)
			{
				Visit(pred.Match);
			}
			public override void Visit(AndPred pred)
			{
				_target.Args.Add(LLPG.GenerateAndPredCheck(_classBody, _currentRule, pred, false));
			}
			public override void Visit(RuleRef rref)
			{
				_target.Args.Add(Node.FromGreen(F.Call(rref.Rule.Name)));
			}
			public override void Visit(TerminalPred term)
			{
				if (term.Set.ContainsEverything)
					_target.Args.Add(LLPG.GenerateMatch());
				else
					_target.Args.Add(LLPG.GenerateMatch(_classBody, _currentRule, term.Set));
			}
		}
	}
}
