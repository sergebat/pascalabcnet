﻿using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using PascalABCCompiler.SyntaxTree;
using PascalABCCompiler.TreeRealization;

using SyntaxTreeBuilder = PascalABCCompiler.SyntaxTree.SyntaxTreeBuilder;
using SymTable = SymbolTable;
using array_const = PascalABCCompiler.TreeRealization.array_const;
using compiler_directive = PascalABCCompiler.TreeRealization.compiler_directive;
using empty_statement = PascalABCCompiler.TreeRealization.empty_statement;
using for_node = PascalABCCompiler.TreeRealization.for_node;
using goto_statement = PascalABCCompiler.TreeRealization.goto_statement;
using if_node = PascalABCCompiler.TreeRealization.if_node;
using labeled_statement = PascalABCCompiler.TreeRealization.labeled_statement;
using question_colon_expression = PascalABCCompiler.TreeRealization.question_colon_expression;
using repeat_node = PascalABCCompiler.TreeRealization.repeat_node;
using sizeof_operator = PascalABCCompiler.TreeRealization.sizeof_operator;
using typeof_operator = PascalABCCompiler.TreeRealization.typeof_operator;
using while_node = PascalABCCompiler.TreeRealization.while_node;
using TreeConverter.LambdaExpressions.Closure;
using TreeConverter.LambdaExpressions;

namespace PascalABCCompiler.TreeConverter
{
    public partial class syntax_tree_visitor // SSM 02.01.17 менять на визитор с другим порядком обхода можно, но бессмысленно
    {

        public override void visit(SyntaxTree.foreach_stmt _foreach_stmt)
        {
            var lambdaSearcher = new LambdaSearcher(_foreach_stmt.in_what);
            if (lambdaSearcher.CheckIfContainsLambdas())
            {
                AddError(new LambdasNotAllowedInForeachInWhatSatetement(get_location(lambdaSearcher.FoundLambda)));
            }

            expression_node foreachCollection = convert_strong(_foreach_stmt.in_what);

            // SSM 29.07.16 - если in_what - одномерный массив, то заменить код foreach на for
            // if (OptimizeForeachInCase1DArray(_foreach_stmt, in_what)) return;

            statements_list sl2 = new statements_list(get_location(_foreach_stmt));
            convertion_data_and_alghoritms.statement_list_stack_push(sl2);

            expression_node tmp = convert_if_typed_expression_to_function_call(foreachCollection);
            if (tmp.type != null)
                foreachCollection = tmp;

            bool sys_coll_ienum; // true означает, что мы нашли тип object у интерфейса System.Collections.IEnumerable
            type_node elem_type = null;
            if (!FindIEnumerableElementType(foreachCollection.type, ref elem_type, out sys_coll_ienum))
                AddError(foreachCollection.location, "CAN_NOT_EXECUTE_FOREACH_BY_EXPR_OF_TYPE_{0}", foreachCollection.type.name);

            var_definition_node foreachVariable = null;
            if (_foreach_stmt.type_name == null) // значит, переменная определена в другом месте
            {
                location loc1 = get_location(_foreach_stmt.identifier);
                definition_node dn = context.check_name_node_type(_foreach_stmt.identifier.name, loc1,
                    general_node_type.variable_node);
                foreachVariable = (var_definition_node)dn;
                if (!check_name_in_current_scope(_foreach_stmt.identifier.name))
                    AddError(loc1, "FOREACH_LOOP_CONTROL_MUST_BE_SIMPLE_LOCAL_VARIABLE");
            }
            else
            {
                foreachVariable = context.add_var_definition(_foreach_stmt.identifier.name, get_location(_foreach_stmt.identifier));

                type_node tn;
                if (_foreach_stmt.type_name is SyntaxTree.no_type_foreach) // значит, это for var x in a
                {
                    tn = elem_type;
                }
                else // значит, это for var x: T in a
                {
                    tn = convert_strong(_foreach_stmt.type_name);
                    check_for_type_allowed(tn, get_location(_foreach_stmt.type_name));
                }

                context.close_var_definition_list(tn, null);
            }

            if (!(foreachVariable.type is compiled_generic_instance_type_node) && !sys_coll_ienum)
                convertion_data_and_alghoritms.check_convert_type_with_inheritance(elem_type, foreachVariable.type, get_location(_foreach_stmt.identifier));

            statements_list sl = new statements_list(get_location(_foreach_stmt.stmt));
            convertion_data_and_alghoritms.statement_list_stack_push(sl);
            CheckToEmbeddedStatementCannotBeADeclaration(_foreach_stmt.stmt);
            foreach_node foreachNode = new foreach_node(foreachVariable, foreachCollection, null, get_location(_foreach_stmt));
            context.cycle_stack.push(foreachNode);
            context.loop_var_stack.Push(foreachVariable);
            context.enter_code_block_with_bind();
            statement_node body = convert_strong(_foreach_stmt.stmt);
            context.leave_code_block();
            context.loop_var_stack.Pop();
            sl = convertion_data_and_alghoritms.statement_list_stack.pop();

            if (sl.statements.Count > 0 || sl.local_variables.Count > 0)
            {
                sl.statements.AddElement(body);
                body = sl;
            }

            foreachNode.what_do = body;

            convertion_data_and_alghoritms.statement_list_stack.pop();
            sl2.statements.AddElement(foreachNode);
            context.cycle_stack.pop();

            return_value(sl2);
        }

        /// <summary>
        /// Преобразует foreach в for, если коллекция это одномерный массив.
        /// </summary>
        /// <param name="_foreach_stmt"></param>
        /// <param name="in_what"></param>
        /// <returns>True - если преобразование удалось, иначе False</returns>
        private bool OptimizeForeachInCase1DArray(foreach_stmt _foreach_stmt, expression_node in_what)
        {
            var is1dimdynarr = false;
            var comptn = in_what.type as compiled_type_node;
            if (comptn != null && comptn.type_special_kind == SemanticTree.type_special_kind.array_kind && comptn.rank == 1)
            {
                is1dimdynarr = true;
            }
            if (!is1dimdynarr)
            {
                var comtn = in_what.type as common_type_node;
                if (comtn != null && comtn.internal_type_special_kind == SemanticTree.type_special_kind.array_kind &&
                    comtn.rank == 1)
                {
                    is1dimdynarr = true;
                }
            }


            // SSM 23.08.16 Закомментировал оптимизацию. Не работает с лямбдами. Лямбды обходят старое дерево. А заменить foreach на for на этом этапе пока не получается - не развита инфраструктура

            if (is1dimdynarr) // Замена foreach на for для массива
            {
                // сгенерировать код для for и вызвать соответствующий visit
                var arrid = GenIdentName();
                //var vdarr = new var_statement(arrid, new semantic_addr_value(in_what)); // semantic_addr_value - перевод в синтаксис для мгновенного вычисления семантического выражения, которое уже вычислено в in_what
                var vdarr = new var_statement(arrid, _foreach_stmt.in_what);

                var i = GenIdentName();
                var x = _foreach_stmt.identifier;

                // Возможны 3 случая:
                // 1. _foreach_stmt.type_name = null - значит, переменная определена в другом месте
                // 2. _foreach_stmt.type_name = no_type_foreach - значит, это for var x in a
                // 3. _foreach_stmt.type_name = T - значит, это for var x: T in a

                statement vd;
                if (_foreach_stmt.type_name == null) // 1.
                    vd = new assign(x, arrid.indexer(i));
                else if (_foreach_stmt.type_name is no_type_foreach) // 2.
                    vd = new var_statement(x, arrid.indexer(i));
                else // 3.
                    vd = new var_statement(x, _foreach_stmt.type_name, arrid.indexer(i));

                // Превратить старое тело в statement_list и добавить к нему в начало x := a[i] или var x := a[i] 
                var newbody = _foreach_stmt.stmt.ToStatementList();
                newbody.AddFirst(vd);

                var high = arrid.dot_node("Length").Minus(1);

                var fornode = new SyntaxTree.for_node(i, 0, high, newbody, for_cycle_type.to, null, null, true);

                var stl = new SyntaxTree.statement_list(vdarr, fornode);
                // Замена 1 оператора на 1 оператор. Всё хорошо даже если оператор помечен меткой
                ReplaceUsingParent(_foreach_stmt, stl);

                visit(stl);
                //visit(vdarr);
                //visit(fornode);

                return true;
            }
            /// SSM 29.07.16 
            return false;
        }
    }
}
