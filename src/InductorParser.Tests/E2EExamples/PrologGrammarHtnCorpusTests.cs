using System;
using System.Collections.Generic;
using InductorParser;
using NUnit.Framework;
using static InductorParser.Tests.TestHelpers;

namespace InductorParser.Tests;

// Corpus tests ported from InductorHtn's HTN-flavored test files
// (https://github.com/EricZinda/InductorHtn). The C# basic parser tests
// already cover everything in InductorProlog's PrologCompilerTests.cpp
// (https://github.com/EricZinda/InductorProlog). This file pulls in the
// parser inputs that the C++ HTN tests rely on but that the existing
// corpus doesn't reach. Both repos are MIT licensed, same author as this
// project.
//
// Sources (all under https://github.com/EricZinda/InductorHtn/blob/master/):
//   src/Tests/Htn/HtnCompilerTests.cpp
//   src/Tests/Htn/HtnPlannerTests.cpp
//   src/Tests/Prolog/HtnGoalResolverTests.cpp
//   src/Tests/Prolog/HtnRuleSetTests.cpp
//   src/Tests/Prolog/HtnTermTests.cpp
//
// Every input here is something a C++ HtnCompiler / HtnGoalResolver / PrologQueryCompiler
// successfully accepts. If the C# parser rejects any of these, the failing input is a bug
// in this parser, not a bad test, per the porting instruction.
[TestFixture]
public class PrologGrammarHtnCorpusTests
{
    // HTN method/operator forms: `if(...)`, `do(...)`, `del(...)`, `add(...)`, `try(...)`,
    // `else,`, `anyOf,`, `allOf,`. Compiled by HtnCompiler in C++.
    private static readonly string[] HtnMethodOperatorDocuments =
    {
        // Basic method + operator pair.
        "test(?A) :- if(true), do( foo(?A) ). \r\nfoo(?A) :- del(), add(). \r\n",

        // Single method (operator missing, parser-side this is still valid).
        "test(?A) :- if(true), do( foo(?A) ). \r\n",

        // try() wrapping a task.
        "test(?A) :- if(true), do( try(foo(?A)) ). \r\n",

        // Empty do() body.
        "test(?A) :- if(bill(?A)), do( ). \r\n",

        // sortBy + > inside if.
        "test(?A) :- if( sortBy(?A, >(bill(?A))) ), do( ). \r\n",

        // Two methods that loop on each other.
        "test(?A) :- if(true), do( foo(?A) ). \r\nfoo(?A) :- if(true), do( test(?A) ). \r\n",

        // Three-way loop with try() around each task.
        "test(?A) :- if(true), do( try(foo(?A)) ). \r\nfoo(?A) :- if(true), do( try(bar(?A)) ). \r\nbar(?A) :- if(true), do( try(test(?A)) ). \r\n",

        // Mixed Prolog rules and HTN methods in the same document.
        "a1(A) :- a2(A). \r\na2(B) :- a1(B). \r\ntest(?A) :- if(true), do( try(foo(?A)) ). \r\nfoo(?A) :- if(true), do( ). \r\n",

        // failureContext / failTask convention.
        "failInCriteria(?Value) :- if(false), do(trace(?Value)).trace(?Value) :- del(), add(?Value). \r\ngoals(failInCriteria(test)).\r\n",
        "failInCriteria(?Value) :- if(failureContext(tag, 1), false), do(trace(?Value)).trace(?Value) :- del(), add(?Value). \r\ngoals(failInCriteria(test)).\r\n",
        "failInCriteria(?Value) :- if(=(?X, 1), failureContext(tag, 1), failTask([1,2,3]) ), do(trace(?Value)).failInCriteria(?Value) :- if(failureContext(tag, 2), failTask([1,2]) ), do(trace(?Value)).trace(?Value) :- del(), add(?Value). \r\nfailTask([]) :- false.failTask([_|T]) :- failTask(T).goals(failInCriteria(test)).\r\n",

        // Empty goals().
        "trace(?Value) :- del(), add(?Value). \r\ngoals().\r\n",

        // goals() with a single term.
        "trace(?Value) :- del(), add(?Value). \r\ngoals(trace(Test1)).\r\n",

        // method with capital-leading functors (HTN flavor).
        "IsTrue(Test1). \r\ntrace(?Value) :- del(), add(?Value). \r\nmethod(?Value) :- if(IsTrue(?Value)), do(trace(?Value)). \r\ngoals(method(Test1)).\r\n",
        "IsTrue(Test1). Alternative(Alternative1). Alternative(Alternative2).\r\ntrace(?Value, ?Value2, ?Value3) :- del(), add(item(?Value, ?Value2, ?Value3)). \r\nmethod(?Value) :- if(IsTrue(?Value), Alternative(?Alt)), do(trace(?Value, Method1, ?Alt)). \r\nmethod(?Value) :- if(IsTrue(?Value), Alternative(?Alt)), do(trace(?Value, Method2, ?Alt)). \r\ngoals(method(Test1)).\r\n",

        // Empty if() and empty do().
        "test(?X) :- if(), do(successTask(10, ?X)) .\r\nsuccessTask(?X, ?Y) :- if(number(?X)), do(debugWatch(?X)).\r\ndebugWatch(?x) :- del(), add().\r\nnumber(10).number(12).number(1). \r\ngoals(test(100)).",

        // Negative-number arithmetic passed through parser.
        "trace(?Value, ?Value2) :- del(), add(?Value, ?Value2). \r\nmethod(?Value) :- if(), do(trace(?Value, Method)). \r\ngoals(method(-(1,2))).\r\n",

        // allOf form.
        "method(?Value) :- allOf, if(IsTrue(?Value), Alternative(?Alt)), do(try(deleteTrueIfExists(?Value)), trace(?Value, Method1, ?Alt)). \r\n",
        "method(?Value) :- allOf, if(), do(trace(?Value, Method1, None)). \r\n",

        // anyOf form.
        "method(?Value) :- anyOf, if(IsTrue(?Value), Alternative(?Alt)), do(trace(?Value, Method1, ?Alt)). \r\n",

        // del with arg.
        "deleteTrue(?Value) :- del(IsTrue(?Value)), add(). \r\n",

        // else clause (after Prolog method, allOf, and anyOf).
        "test() :- if(), do(failTask()).\r\ntest() :- else, if(), do(success()).\r\nfailTask() :- if( <(2,1) ), do().\r\nsuccess() :- del(), add(item(success)).\r\n",
        "test() :- allOf, if(), do(failTask()).\r\ntest() :- else, if(), do(success()).\r\n",
        "test() :- anyOf, if(), do(failTask()).\r\ntest() :- else, if(), do(elseSuccess()).\r\n",

        // Multiple try(...) inside do() including empty try().
        "number(10).number(12).number(1). \r\ntest() :- if(), do(try(successTask()), try(failTask()), try(failTask()), try(successTask(?Y)) ).\r\nfailTask() :- if(<(2, 1)), do(debugWatch(fail)).\r\nsuccessTask() :- if(<(1, 2)), do(debugWatch(success)).\r\nsuccessTask(?X) :- if(number(?X)), do(debugWatch(?X)).\r\ndebugWatch(?x) :- del(), add(item(?x)).\r\ngoals(try(), successTask()).\r\n",

        // first(...) inside if + arithmetic via is/+.
        "test() :- if( first(number(?A)), is(?B, +(?A, ?A)) ), do(debugWatch(?B)).\r\n",

        // sortBy as a goal.
        "test() :- if( sortBy(?A, <(number(?A))) ), do(debugWatch(?A)).\r\n",

        // Nested first(sortBy(...)).
        "test(?C) :- if( first( sortBy(?A, <(number(?A)))), is(?B, +(?A, ?A)) ), do(debugWatch(?C)).\r\n",

        // not(...) inside if.
        "test() :- if( person(?X), not(isFunny(?X)) ), do(debugWatch(?X)).\r\n",

        // SHOP-style HTN with hyphenated functor names and multi-arg del/add.
        "have-taxi-fare(?distance) :- have-cash(?m), >=(?m, +(1.5, ?distance)). \r\nwalking-distance(?u,?v) :- weather-is(good), distance(?u,?v,?w), =<(?w, 3). \r\nwalking-distance(?u,?v) :- distance(?u,?v,?w), =<(?w, 0.5). \r\npay-driver(?fare) :- if(have-cash(?m), >=(?m, ?fare)), do(set-cash(?m, -(?m,?fare))). \r\ntravel-to(?q) :- if(at(?p), walking-distance(?p, ?q)), do(walk(?p, ?q)). \r\ntravel-to(?y) :- if(first(at(?x), at-taxi-stand(?t, ?x), distance(?x, ?y, ?d), have-taxi-fare(?d))), do(hail(?t,?x), ride(?t, ?x, ?y), pay-driver(+(1.50, ?d))). \r\nhail(?vehicle, ?location) :- del(), add(at(?vehicle, ?location)). \r\nride(?vehicle, ?a, ?b) :- del(at(?a), at(?vehicle, ?a)), add(at(?b), at(?vehicle, ?b)). \r\nset-cash(?old, ?new) :- del(have-cash(?old)), add(have-cash(?new)). \r\n",

        // Recursive Prolog rule used to stress the planner's budget.
        "gen(?Cur, ?Top, ?Cur) :- =<(?Cur, ?Top).\r\ngen(?Cur, ?Top, ?Next):- =<(?Cur, ?Top), is(?Cur1, +(?Cur, 1)), gen(?Cur1, ?Top, ?Next).\r\nblowBudget() :- if(gen(0, 10000,?S)), do(trace(SHOULDNEVERHAPPEN)).\r\ntrace(?Value) :- del(), add(?Value). \r\ngoals(trace(Test), blowBudget()).\r\n",
    };

    // HTN-style Prolog programs: built-ins (atom_chars, atomic, count, etc.), cut as
    // an argument, anonymous _, comments interleaved through the body, escaped-quote atoms.
    private static readonly string[] HtnPrologDocuments =
    {
        // Rule with empty body after `:-`.
        "trace(?x) :- .\r\n",
        "itemsInBag(Name1, ?Count) :- . \r\n",

        // Head/tail destructuring.
        "split([?Head | ?Tail], ?Head, ?Tail). goals(split([a, b, c, d], ?Head, ?Tail)).\r\n",

        // Document with `%` line comments interleaved through the body.
        "member(?X, [?X|_]).        % member(X, [Head|Tail]) is true if X = Head \r\n                         % that is, if X is the head of the list\r\nmember(?X, [_|?Tail]) :-   % or if X is a member of Tail,\r\n  member(?X, ?Tail).       % ie. if member(X, Tail) is true.\r\ngoals( member(a, [b, c, a, [d, e, f]]), not(member(d, [b, c, a, [d, e, f]])) ).\r\n",

        // append + reverse over lists of compound terms.
        "append([], ?Ys, ?Ys).append([?X|?Xs], ?Ys, [?X|?Zs]) :- append(?Xs, ?Ys, ?Zs).reverse([],[]).reverse([?X|?Xs],?YsX) :- reverse(?Xs,?Ys), append(?Ys,[?X],?YsX).goals( reverse([a, b, foo(a, [a, b, c])], ?X) ).\r\n",

        // Cut `!` at end of body.
        "len([], 0).\r\nlen([_ | ?Tail], ?Length) :-\r\n    len(?Tail, ?Length1),\r\n    is(?Length, +(?Length1, 1)),!.\r\ngoals( len([[], b, foo(a, [a, b, c])], ?X) ).\r\n",

        // atom_chars built-in calls (parser-side these are just functors).
        "goals(=(?X, pre), atom_chars(foo, ?List), =(?Y, ?X), =(?Z, ?List) ).\r\n",
        "goals(=(?X, pre), atom_chars(?List, [f, o, o]), =(?Y, ?X), =(?Z, ?List) ).\r\n",
        "goals(=(?X, pre), atom_chars(foo, [?FirstChar | _]), =(?Y, ?X), =(?Z, ?FirstChar) ).\r\n",

        // Single-quoted atoms with spaces and special characters.
        "goals(downcase_atom('THIS IS A TEST', ?x) ).\r\n",
        "goals(atom_concat(a, b, ?x) ).\r\n",

        // Single-quoted atom with embedded escaped double quotes.
        "goals( write('Test \"of the emergency\"') ).\r\n",

        // Bare functor `nl` (no parens).
        "goals( nl ).\r\n",
        "goals( writeln('test') ).\r\n",

        // forall(...) with a single goal and with multiple goals.
        "goals( forall(item(?X), rule(item(?X))) ).\r\n",
        "goals( forall( item(?X), rule(item(?X)) ), writeln(item(?X)) ).\r\n",

        // atomic(...) variants over different argument shapes.
        "goals(atomic(mia)).\r\n",
        "goals(atomic(mia())).\r\n",
        "goals(atomic(8)).\r\n",
        "goals(atomic(3.25)).\r\n",
        "goals(atomic(loves(vincent, mia))).\r\n",
        "goals(atomic(?X)).\r\n",

        // true / false as bare functors.
        "goals( true ).\r\n",
        "goals( false ).\r\n",

        // Rule with a cut mid-body, two clauses.
        "rule(?X) :- itemsInBag(?X), !.rule(?X) :- =(?X, good).goals( rule(?X) ).\r\n",

        // count / min / max with cut as the last argument.
        "goals( count(?Count, itemsInBag(?X), !) ).\r\n",
        "goals( min(?Min, ?Size, itemsInBag(?X, ?Size), !) ).\r\n",

        // assert / retract / retractall.
        "goals( assert(itemsInBag(Name3)), itemsInBag(?After) ).\r\n",
        "goals( retract(itemsInBag(Name1)), itemsInBag(?After) ).\r\n",
        "goals( retractall(itemsInBag(?X)) ).\r\n",

        // min over a multi-argument item.
        "goals( min(?Total, ?ItemCount, itemsInBag(?Name, ?ItemCount)) ).\r\n",

        // distinct(...) variants.
        "goals( distinct(_, test(_)) ).\r\n",
        "goals( distinct(_, letter(?X)) ).\r\n",
        "goals( distinct(_, letter(?X), letter(?Y)) ).\r\n",
        "goals( distinct(?X, letter(?X)) ).\r\n",

        // sortBy with a comparator-functor wrapping multiple goals.
        "goals(sortBy(?C, <(letter(?X), capital(?X), cost(?X, ?C)))).\r\n",

        // == and \== operators.
        "goals(==(letter(a), letter(b))).\r\n",
        "goals( ==(letter(a), letter(a)) ).\r\n",
        "goals(\\==(letter(a), letter(a))).\r\n",
        "goals(\\==(letter(a), letter(b))).\r\n",

        // is(...) with arithmetic.
        "goals(is(1, 1)).\r\n",
        "goals(is(+(1, 1), +(0, 2))).\r\n",
        "goals(is(?X, +(1,2))).\r\n",

        // not(...).
        "goals(not(letter(a))).\r\n",
        "goals(capital(?Capital), not(letter(d)), letter(?y)).\r\n",

        // first(...) at top level and nested.
        "goals(first(letter(?x))).\r\n",
        "goals(first(capital(?Capital), first(letter(?x)), letter(?y))).\r\n",

        // print(...) call.
        "goals(letter(?X), print(?X), capital(?X)).\r\n",

        // Anonymous `_` in head, body, and goal.
        "itemsInBag(Name1). itemsInBag(Name2). rule(?X) :- itemsInBag(_), itemsInBag(?X).goals( rule(_) ).\r\n",
        "test(_, _) :- test2(_, _). \r\ntest2(_, _). \r\ngoals( test(a, b) ).\r\n",
    };

    // Single queries (compiled by PrologQueryCompiler in C++). These exercise the
    // grammar's Query rule, not Document.
    private static readonly string[] HtnQueries =
    {
        // Single-quoted atoms with different special characters and numeric edge
        // cases all in one query.
        "d_named('Plage'), !, d_named('_flage'), d_named('Pla ge'), d_named('Pla!ge'), d_named('pl_age'), d_named('pl1age'), d_named(12.4), d_named(5), d_named('5a'), d_named([]).",

        // Double-quoted string preserved verbatim through the parser.
        "vocabulary(\"blue\", Pred, Argcount, adjective, X).",

        // Deeply nested list expressions used by HtnTermTests for ToString round-trip.
        "=(?X, []).",
        "=(?X, [a]).",
        "=(?X, [a,b]).",
        "=(?X, [a,b,[]]).",
        "=(?X, [[],a,b,[]]).",
        "=(?X, [[],[],[]]).",
        "=(?X, [[a(b,c),[]],[],[]]).",
        "=(?X, [a([a,[a([b],d)],c]),[]]).",
    };

    [Test]
    public void HTN_method_operator_documents_parse()
    {
        AssertAllParse(PrologGrammar.Htn.Document, HtnMethodOperatorDocuments, "Htn.Document (HTN method/operator)");
    }

    [Test]
    public void HTN_prolog_documents_parse()
    {
        AssertAllParse(PrologGrammar.Htn.Document, HtnPrologDocuments, "Htn.Document (HTN-flavored Prolog)");
    }

    [Test]
    public void HTN_queries_parse()
    {
        AssertAllParse(PrologGrammar.Htn.Query, HtnQueries, "Htn.Query");
    }

}
