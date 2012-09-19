﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.IO;
using System.Diagnostics;
using ICSharpCode.NRefactory.PatternMatching;
using ICSharpCode.NRefactory.TypeSystem;
using System.Globalization;
using ICSharpCode.NRefactory.Cpp.Ast;
using Attribute = ICSharpCode.NRefactory.Cpp.Ast.Attribute;
using ICSharpCode.NRefactory.Cpp.Formatters;

namespace ICSharpCode.NRefactory.Cpp
{
    public class CppOutputVisitor : IAstVisitor<object, object>
    {
        IOutputFormatter formatter;
        readonly CppFormattingOptions policy;

        //<ÑAPA>
        readonly List<AstNode> headerNodes = new List<AstNode>();
        //</ÑAPA>

        readonly Stack<AstNode> containerStack = new Stack<AstNode>();
        readonly Stack<AstNode> positionStack = new Stack<AstNode>();

        private List<string> currNamespaces;
        public static string WorkingPath;
        private bool isGenericTemplate;


        /// <summary>
        /// Used to insert the minimal amount of spaces so that the lexer recognizes the tokens that were written.
        /// </summary>
        LastWritten lastWritten;

        enum LastWritten
        {
            Whitespace,
            Other,
            KeywordOrIdentifier,
            Plus,
            Minus,
            Ampersand,
            QuestionMark,
            Division
        }

        public CppOutputVisitor(TextWriter textWriter, CppFormattingOptions formattingPolicy)
        {
            if (textWriter == null)
                throw new ArgumentNullException("textWriter");
            if (formattingPolicy == null)
                throw new ArgumentNullException("formattingPolicy");
            this.formatter = new TextWriterOutputFormatter(textWriter);
            this.policy = formattingPolicy;
            this.isGenericTemplate = false;
        }

        public CppOutputVisitor(IOutputFormatter formatter, CppFormattingOptions formattingPolicy)
        {
            if (formatter == null)
                throw new ArgumentNullException("formatter");
            if (formattingPolicy == null)
                throw new ArgumentNullException("formattingPolicy");
            this.formatter = formatter;
            this.policy = formattingPolicy;
            this.isGenericTemplate = false;
        }

        void StartNode(AstNode node)
        {
            // Ensure that nodes are visited in the proper nested order.
            // Jumps to different subtrees are allowed only for the child of a placeholder node.

            //SAFE TO IGNORE THIS. IN C++ THE HEADER FILE OPENS THE NAMESPACE NODE AGAIN. SURE THAT IT IS NOT IN THE PROPER NESTED ORDER
            //Debug.Assert(containerStack.Count == 0 || node.Parent == containerStack.Peek() || containerStack.Peek().NodeType == NodeType.Pattern);
            if (positionStack.Count > 0)
                WriteSpecialsUpToNode(node);
            containerStack.Push(node);
            positionStack.Push(node.FirstChild);
            formatter.StartNode(node);
        }

        object EndNode(AstNode node)
        {
            Debug.Assert(node == containerStack.Peek());
            AstNode pos = positionStack.Pop();
            Debug.Assert(pos == null || pos.Parent == node);
            WriteSpecials(pos, null);
            containerStack.Pop();
            formatter.EndNode(node);
            return null;
        }

        #region Write tokens
        /// <summary>
        /// Writes a keyword, and all specials up to
        /// </summary>
        void WriteKeyword(string keyword, Role<CppTokenNode> tokenRole = null)
        {
            WriteSpecialsUpToRole(tokenRole ?? AstNode.Roles.Keyword);
            if (lastWritten == LastWritten.KeywordOrIdentifier)
                formatter.Space();
            formatter.WriteKeyword(keyword);
            lastWritten = LastWritten.KeywordOrIdentifier;
        }

        void WriteIdentifier(string identifier, Role<Identifier> identifierRole = null)
        {
            WriteSpecialsUpToRole(identifierRole ?? AstNode.Roles.Identifier);
            if (IsKeyword(identifier, containerStack.Peek()))
            {
                if (lastWritten == LastWritten.KeywordOrIdentifier)
                    Space(); // this space is not strictly required, so we call Space()
                formatter.WriteToken("@");
            }
            else if (lastWritten == LastWritten.KeywordOrIdentifier)
            {
                formatter.Space(); // this space is strictly required, so we directly call the formatter
            }
            formatter.WriteIdentifier(identifier);
            lastWritten = LastWritten.KeywordOrIdentifier;
        }

        void WriteToken(string token, Role<CppTokenNode> tokenRole)
        {
            WriteSpecialsUpToRole(tokenRole);
            // Avoid that two +, - or ? tokens are combined into a ++, -- or ?? token.
            // Note that we don't need to handle tokens like = because there's no valid
            // C# program that contains the single token twice in a row.
            // (for +, - and &, this can happen with unary operators;
            // for ?, this can happen in "a is int? ? b : c" or "a as int? ?? 0";
            // and for /, this can happen with "1/ *ptr" or "1/ //comment".)
            if (lastWritten == LastWritten.Plus && token[0] == '+'
                || lastWritten == LastWritten.Minus && token[0] == '-'
                || lastWritten == LastWritten.Ampersand && token[0] == '&'
                || lastWritten == LastWritten.QuestionMark && token[0] == '?'
                || lastWritten == LastWritten.Division && token[0] == '*')
            {
                formatter.Space();
            }
            formatter.WriteToken(token);
            if (token == "+")
                lastWritten = LastWritten.Plus;
            else if (token == "-")
                lastWritten = LastWritten.Minus;
            else if (token == "&")
                lastWritten = LastWritten.Ampersand;
            else if (token == "?")
                lastWritten = LastWritten.QuestionMark;
            else if (token == "/")
                lastWritten = LastWritten.Division;
            else
                lastWritten = LastWritten.Other;
        }

        void LPar()
        {
            WriteToken("(", AstNode.Roles.LPar);
        }

        void RPar()
        {
            WriteToken(")", AstNode.Roles.RPar);
        }

        /// <summary>
        /// Marks the end of a statement
        /// </summary>
        void Semicolon()
        {
            Role role = containerStack.Peek().Role; // get the role of the current node
            if (!(role == ForStatement.InitializerRole || role == ForStatement.IteratorRole || role == UsingNamespaceStatement.ResourceAcquisitionRole))
            {
                WriteToken(";", AstNode.Roles.Semicolon);
                NewLine();
            }
        }

        /// <summary>
        /// Writes a space depending on policy.
        /// </summary>
        void Space(bool addSpace = true)
        {
            if (addSpace)
            {
                formatter.Space();
                lastWritten = LastWritten.Whitespace;
            }
        }

        void NewLine()
        {
            formatter.NewLine();
            lastWritten = LastWritten.Whitespace;
        }

        void OpenBrace(BraceStyle style)
        {
            WriteSpecialsUpToRole(AstNode.Roles.LBrace);
            formatter.OpenBrace(style);
            lastWritten = LastWritten.Other;
        }

        void CloseBrace(BraceStyle style)
        {
            WriteSpecialsUpToRole(AstNode.Roles.RBrace);
            formatter.CloseBrace(style);
            lastWritten = LastWritten.Other;
        }

        #endregion

        #region WriteSpecials
        /// <summary>
        /// Writes all specials from start to end (exclusive). Does not touch the positionStack.
        /// </summary>
        void WriteSpecials(AstNode start, AstNode end)
        {
            for (AstNode pos = start; pos != end; pos = pos.NextSibling)
            {
                if (pos.Role == AstNode.Roles.Comment || pos.Role == AstNode.Roles.PreProcessorDirective)
                {
                    pos.AcceptVisitor(this, null);
                }
            }
        }

        /// <summary>
        /// Writes all specials between the current position (in the positionStack) and the next
        /// node with the specified role. Advances the current position.
        /// </summary>
        void WriteSpecialsUpToRole(Role role)
        {
            WriteSpecialsUpToRole(role, null);
        }

        void WriteSpecialsUpToRole(Role role, AstNode nextNode)
        {
            if (positionStack.Count == 0)
                return;
            // Look for the role between the current position and the nextNode.
            for (AstNode pos = positionStack.Peek(); pos != null && pos != nextNode; pos = pos.NextSibling)
            {
                if (pos.Role == role)
                {
                    WriteSpecials(positionStack.Pop(), pos);
                    // Push the next sibling because the node matching the role is not a special,
                    // and should be considered to be already handled.
                    positionStack.Push(pos.NextSibling);
                    // This is necessary for OptionalComma() to work correctly.
                    break;
                }
            }
        }

        /// <summary>
        /// Writes all specials between the current position (in the positionStack) and the specified node.
        /// Advances the current position.
        /// </summary>
        void WriteSpecialsUpToNode(AstNode node)
        {
            if (positionStack.Count == 0)
                return;
            for (AstNode pos = positionStack.Peek(); pos != null; pos = pos.NextSibling)
            {
                if (pos == node)
                {
                    WriteSpecials(positionStack.Pop(), pos);
                    // Push the next sibling because the node itself is not a special,
                    // and should be considered to be already handled.
                    positionStack.Push(pos.NextSibling);
                    // This is necessary for OptionalComma() to work correctly.
                    break;
                }
            }
        }
        #endregion

        #region Expressions
        public object VisitAnonymousMethodExpression(AnonymousMethodExpression anonymousMethodExpression, object data)
        {
            StartNode(anonymousMethodExpression);
            if (anonymousMethodExpression.IsAsync)
            {
                WriteKeyword("async", AnonymousMethodExpression.AsyncModifierRole);
                Space();
            }
            WriteKeyword("delegate");
            if (anonymousMethodExpression.HasParameterList)
            {
                Space(policy.SpaceBeforeMethodDeclarationParentheses);
                WriteCommaSeparatedListInParenthesis(anonymousMethodExpression.Parameters, policy.SpaceWithinMethodDeclarationParentheses);
            }
            anonymousMethodExpression.Body.AcceptVisitor(this, data);
            return EndNode(anonymousMethodExpression);
        }

        public object VisitUndocumentedExpression(UndocumentedExpression undocumentedExpression, object data)
        {
            StartNode(undocumentedExpression);
            switch (undocumentedExpression.UndocumentedExpressionType)
            {
                case UndocumentedExpressionType.ArgList:
                case UndocumentedExpressionType.ArgListAccess:
                    WriteKeyword("__arglist");
                    break;
                case UndocumentedExpressionType.MakeRef:
                    WriteKeyword("__makeref");
                    break;
                case UndocumentedExpressionType.RefType:
                    WriteKeyword("__reftype");
                    break;
                case UndocumentedExpressionType.RefValue:
                    WriteKeyword("__refvalue");
                    break;
            }
            if (undocumentedExpression.Arguments.Count > 0)
            {
                Space(policy.SpaceBeforeMethodCallParentheses);
                WriteCommaSeparatedListInParenthesis(undocumentedExpression.Arguments, policy.SpaceWithinMethodCallParentheses);
            }
            return EndNode(undocumentedExpression);
        }

        public object VisitArrayCreateExpression(ArrayCreateExpression arrayCreateExpression, object data)
        {
            StartNode(arrayCreateExpression);
            WriteKeyword("new");
            arrayCreateExpression.Type.AcceptVisitor(this, data);
            if (arrayCreateExpression.Arguments.Count > 0)
                WriteCommaSeparatedListInBrackets(arrayCreateExpression.Arguments);
            foreach (var specifier in arrayCreateExpression.AdditionalArraySpecifiers)
                specifier.AcceptVisitor(this, data);
            arrayCreateExpression.Initializer.AcceptVisitor(this, data);
            return EndNode(arrayCreateExpression);
        }

        public object VisitArrayInitializerExpression(ArrayInitializerExpression arrayInitializerExpression, object data)
        {
            StartNode(arrayInitializerExpression);
            // "new List<int> { { 1 } }" and "new List<int> { 1 }" are the same semantically.
            // We also use the same AST for both: we always use two nested ArrayInitializerExpressions
            // for collection initializers, even if the user did not write nested brackets.
            // The output visitor will output nested braces only if they are necessary,
            // or if the braces tokens exist in the AST.
            bool bracesAreOptional = arrayInitializerExpression.Elements.Count == 1
                && IsObjectOrCollectionInitializer(arrayInitializerExpression.Parent)
                && !CanBeConfusedWithObjectInitializer(arrayInitializerExpression.Elements.Single());
            if (bracesAreOptional && arrayInitializerExpression.LBraceToken.IsNull)
            {
                arrayInitializerExpression.Elements.Single().AcceptVisitor(this, data);
            }
            else
            {
                PrintInitializerElements(arrayInitializerExpression.Elements);
            }
            return EndNode(arrayInitializerExpression);
        }

        bool CanBeConfusedWithObjectInitializer(Expression expr)
        {
            // "int a; new List<int> { a = 1 };" is an object initalizers and invalid, but
            // "int a; new List<int> { { a = 1 } };" is a valid collection initializer.
            AssignmentExpression ae = expr as AssignmentExpression;
            return ae != null && ae.Operator == AssignmentOperatorType.Assign;
        }

        bool IsObjectOrCollectionInitializer(AstNode node)
        {
            if (!(node is ArrayInitializerExpression))
                return false;
            if (node.Parent is ObjectCreateExpression)
                return node.Role == ObjectCreateExpression.InitializerRole;
            if (node.Parent is NamedExpression)
                return node.Role == NamedExpression.Roles.Expression;
            return false;
        }

        void PrintInitializerElements(AstNodeCollection<Expression> elements)
        {
            BraceStyle style;
            if (policy.PlaceArrayInitializersOnNewLine == ArrayInitializerPlacement.AlwaysNewLine)
                style = BraceStyle.NextLine;
            else
                style = BraceStyle.EndOfLine;
            OpenBrace(style);
            bool isFirst = true;
            foreach (AstNode node in elements)
            {
                if (isFirst)
                {
                    isFirst = false;
                }
                else
                {
                    Comma(node, noSpaceAfterComma: true);
                    NewLine();
                }
                node.AcceptVisitor(this, null);
            }
            //OptionalComma();
            NewLine();
            CloseBrace(style);
        }

        public object VisitAssignmentExpression(AssignmentExpression assignmentExpression, object data)
        {
            StartNode(assignmentExpression);
            assignmentExpression.Left.AcceptVisitor(this, data);
            Space(policy.SpaceAroundAssignment);
            WriteToken(AssignmentExpression.GetOperatorSymbol(assignmentExpression.Operator), AssignmentExpression.OperatorRole);
            Space(policy.SpaceAroundAssignment);
            assignmentExpression.Right.AcceptVisitor(this, data);
            return EndNode(assignmentExpression);
        }

        public object VisitBaseReferenceExpression(BaseReferenceExpression baseReferenceExpression, object data)
        {
            StartNode(baseReferenceExpression);
            WriteKeyword("base");
            return EndNode(baseReferenceExpression);
        }

        public object VisitBinaryOperatorExpression(BinaryOperatorExpression binaryOperatorExpression, object data)
        {
            StartNode(binaryOperatorExpression);
            binaryOperatorExpression.Left.AcceptVisitor(this, data);
            bool spacePolicy;
            switch (binaryOperatorExpression.Operator)
            {
                case BinaryOperatorType.BitwiseAnd:
                case BinaryOperatorType.BitwiseOr:
                case BinaryOperatorType.ExclusiveOr:
                    spacePolicy = policy.SpaceAroundBitwiseOperator;
                    break;
                case BinaryOperatorType.ConditionalAnd:
                case BinaryOperatorType.ConditionalOr:
                    spacePolicy = policy.SpaceAroundLogicalOperator;
                    break;
                case BinaryOperatorType.GreaterThan:
                case BinaryOperatorType.GreaterThanOrEqual:
                case BinaryOperatorType.LessThanOrEqual:
                case BinaryOperatorType.LessThan:
                    spacePolicy = policy.SpaceAroundRelationalOperator;
                    break;
                case BinaryOperatorType.Equality:
                case BinaryOperatorType.InEquality:
                    spacePolicy = policy.SpaceAroundEqualityOperator;
                    break;
                case BinaryOperatorType.Add:
                case BinaryOperatorType.Subtract:
                    spacePolicy = policy.SpaceAroundAdditiveOperator;
                    break;
                case BinaryOperatorType.Multiply:
                case BinaryOperatorType.Divide:
                case BinaryOperatorType.Modulus:
                    spacePolicy = policy.SpaceAroundMultiplicativeOperator;
                    break;
                case BinaryOperatorType.ShiftLeft:
                case BinaryOperatorType.ShiftRight:
                    spacePolicy = policy.SpaceAroundShiftOperator;
                    break;
                case BinaryOperatorType.NullCoalescing:
                    spacePolicy = true;
                    break;
                default:
                    throw new NotSupportedException("Invalid value for BinaryOperatorType");
            }
            Space(spacePolicy);
            WriteToken(BinaryOperatorExpression.GetOperatorSymbol(binaryOperatorExpression.Operator), BinaryOperatorExpression.OperatorRole);
            Space(spacePolicy);
            binaryOperatorExpression.Right.AcceptVisitor(this, data);
            return EndNode(binaryOperatorExpression);
        }

        public object VisitCastExpression(CastExpression castExpression, object data)
        {
            StartNode(castExpression);
            LPar();
            Space(policy.SpacesWithinCastParentheses);
            castExpression.Type.AcceptVisitor(this, data);
            Space(policy.SpacesWithinCastParentheses);
            RPar();
            Space(policy.SpaceAfterTypecast);

            //Parenthesis ????????????????????? in C#OutputVisitor there are not
            LPar();
            castExpression.Expression.AcceptVisitor(this, data);
            RPar();
            return EndNode(castExpression);
        }

        public object VisitCheckedExpression(CheckedExpression checkedExpression, object data)
        {
            StartNode(checkedExpression);
            WriteKeyword("checked");
            LPar();
            Space(policy.SpacesWithinCheckedExpressionParantheses);
            checkedExpression.Expression.AcceptVisitor(this, data);
            Space(policy.SpacesWithinCheckedExpressionParantheses);
            RPar();
            return EndNode(checkedExpression);
        }

        public object VisitConditionalExpression(ConditionalExpression conditionalExpression, object data)
        {
            StartNode(conditionalExpression);
            conditionalExpression.Condition.AcceptVisitor(this, data);

            Space(policy.SpaceBeforeConditionalOperatorCondition);
            WriteToken("?", ConditionalExpression.QuestionMarkRole);
            Space(policy.SpaceAfterConditionalOperatorCondition);

            conditionalExpression.TrueExpression.AcceptVisitor(this, data);

            Space(policy.SpaceBeforeConditionalOperatorSeparator);
            WriteToken(":", ConditionalExpression.ColonRole);
            Space(policy.SpaceAfterConditionalOperatorSeparator);

            conditionalExpression.FalseExpression.AcceptVisitor(this, data);

            return EndNode(conditionalExpression);
        }

        public object VisitDefaultValueExpression(DefaultValueExpression defaultValueExpression, object data)
        {
            StartNode(defaultValueExpression);

            WriteKeyword("default");
            LPar();
            Space(policy.SpacesWithinTypeOfParentheses);
            defaultValueExpression.Type.AcceptVisitor(this, data);
            Space(policy.SpacesWithinTypeOfParentheses);
            RPar();

            return EndNode(defaultValueExpression);
        }

        public object VisitDirectionExpression(DirectionExpression directionExpression, object data)
        {
            StartNode(directionExpression);

            switch (directionExpression.FieldDirection)
            {
                case FieldDirection.Out:
                    WriteKeyword("out");
                    break;
                case FieldDirection.Ref:
                    WriteKeyword("ref");
                    break;
                default:
                    throw new NotSupportedException("Invalid value for FieldDirection");
            }
            Space();
            directionExpression.Expression.AcceptVisitor(this, data);

            return EndNode(directionExpression);
        }

        public object VisitIdentifierExpression(IdentifierExpression identifierExpression, object data)
        {
            StartNode(identifierExpression);
            WriteIdentifier(identifierExpression.Identifier);
            WriteTypeArguments(identifierExpression.TypeArguments);
            return EndNode(identifierExpression);
        }

        public object VisitIndexerExpression(IndexerExpression indexerExpression, object data)
        {
            StartNode(indexerExpression);
            //Add parenthesis if the parent is pointer expression: *a[3] is incorrect but (*a)[3] is correct !           
            indexerExpression.Target.AcceptVisitor(this, data);
            if (indexerExpression.Parent is PointerExpression)
                RPar();
            Space(policy.SpaceBeforeMethodCallParentheses);
            WriteCommaSeparatedListInBrackets(indexerExpression.Arguments);
            return EndNode(indexerExpression);
        }

        public object VisitInvocationExpression(InvocationExpression invocationExpression, object data)
        {
            StartNode(invocationExpression);
            invocationExpression.Target.AcceptVisitor(this, data);
            Space(policy.SpaceBeforeMethodCallParentheses);
            WriteCommaSeparatedListInParenthesis(invocationExpression.Arguments, policy.SpaceWithinMethodCallParentheses);
            return EndNode(invocationExpression);
        }

        public object VisitLambdaExpression(LambdaExpression lambdaExpression, object data)
        {
            StartNode(lambdaExpression);
            if (lambdaExpression.IsAsync)
            {
                WriteKeyword("async", LambdaExpression.AsyncModifierRole);
                Space();
            }
            if (LambdaNeedsParenthesis(lambdaExpression))
            {
                WriteCommaSeparatedListInParenthesis(lambdaExpression.Parameters, policy.SpaceWithinMethodDeclarationParentheses);
            }
            else
            {
                lambdaExpression.Parameters.Single().AcceptVisitor(this, data);
            }
            Space();
            WriteToken("=>", LambdaExpression.ArrowRole);
            Space();
            lambdaExpression.Body.AcceptVisitor(this, data);
            return EndNode(lambdaExpression);
        }

        bool LambdaNeedsParenthesis(LambdaExpression lambdaExpression)
        {
            if (lambdaExpression.Parameters.Count != 1)
                return true;
            var p = lambdaExpression.Parameters.Single();
            return !(p.Type.IsNull && p.ParameterModifier == ParameterModifier.None);
        }

        public object VisitMemberReferenceExpression(MemberReferenceExpression memberReferenceExpression, object data)
        {
            StartNode(memberReferenceExpression);

            //Expressions like new MyObject().f1() ---->  new MyObject()->f1() is incorrect, must be (new MyObject())->f1()
            if (memberReferenceExpression.Target is ObjectCreateExpression && memberReferenceExpression.Parent is InvocationExpression)
                LPar();

            memberReferenceExpression.Target.AcceptVisitor(this, data);

            if (memberReferenceExpression.Target is ObjectCreateExpression && memberReferenceExpression.Parent is InvocationExpression)
                RPar();

            if (memberReferenceExpression.FirstChild is TypeReferenceExpression)
            {
                WriteToken("::", MemberReferenceExpression.Roles.Dot);
            }
            else
            {
                WriteToken("->", MemberReferenceExpression.Roles.Dot);
            }

            WriteIdentifier(memberReferenceExpression.MemberName);
            WriteTypeArguments(memberReferenceExpression.TypeArguments);
            return EndNode(memberReferenceExpression);
        }

        public object VisitNamedArgumentExpression(NamedArgumentExpression namedArgumentExpression, object data)
        {
            StartNode(namedArgumentExpression);
            WriteIdentifier(namedArgumentExpression.Identifier);
            WriteToken(":", NamedArgumentExpression.Roles.Colon);
            Space();
            namedArgumentExpression.Expression.AcceptVisitor(this, data);
            return EndNode(namedArgumentExpression);
        }

        public object VisitNamedExpression(NamedExpression namedExpression, object data)
        {
            StartNode(namedExpression);
            WriteIdentifier(namedExpression.Identifier);
            Space();
            WriteToken("=", NamedArgumentExpression.Roles.Assign);
            Space();
            namedExpression.Expression.AcceptVisitor(this, data);
            return EndNode(namedExpression);
        }

        public object VisitNullReferenceExpression(NullReferenceExpression nullReferenceExpression, object data)
        {
            StartNode(nullReferenceExpression);
            WriteKeyword("null");
            return EndNode(nullReferenceExpression);
        }

        public object VisitObjectCreateExpression(ObjectCreateExpression objectCreateExpression, object data)
        {
            StartNode(objectCreateExpression);
            WriteKeyword("new");
            objectCreateExpression.Type.AcceptVisitor(this, data);
            bool useParenthesis = objectCreateExpression.Arguments.Any() || objectCreateExpression.Initializer.IsNull;
            // also use parenthesis if there is an '(' token
            if (!objectCreateExpression.LParToken.IsNull)
                useParenthesis = true;
            if (useParenthesis)
            {
                Space(policy.SpaceBeforeMethodCallParentheses);
                WriteCommaSeparatedListInParenthesis(objectCreateExpression.Arguments, policy.SpaceWithinMethodCallParentheses);
            }
            objectCreateExpression.Initializer.AcceptVisitor(this, data);
            return EndNode(objectCreateExpression);
        }

        public object VisitAnonymousTypeCreateExpression(AnonymousTypeCreateExpression anonymousTypeCreateExpression, object data)
        {
            StartNode(anonymousTypeCreateExpression);
            WriteKeyword("new");
            PrintInitializerElements(anonymousTypeCreateExpression.Initializers);
            return EndNode(anonymousTypeCreateExpression);
        }

        public object VisitParenthesizedExpression(ParenthesizedExpression parenthesizedExpression, object data)
        {
            StartNode(parenthesizedExpression);
            LPar();
            Space(policy.SpacesWithinParentheses);
            parenthesizedExpression.Expression.AcceptVisitor(this, data);
            Space(policy.SpacesWithinParentheses);
            RPar();
            return EndNode(parenthesizedExpression);
        }

        public object VisitPointerReferenceExpression(PointerReferenceExpression pointerReferenceExpression, object data)
        {
            StartNode(pointerReferenceExpression);
            pointerReferenceExpression.Target.AcceptVisitor(this, data);
            WriteToken("->", PointerReferenceExpression.ArrowRole);
            WriteIdentifier(pointerReferenceExpression.MemberName);
            WriteTypeArguments(pointerReferenceExpression.TypeArguments);
            return EndNode(pointerReferenceExpression);
        }

        public object VisitEmptyExpression(EmptyExpression emptyExpression, object data)
        {
            StartNode(emptyExpression);
            return EndNode(emptyExpression);
        }

        #region VisitPrimitiveExpression
        public object VisitPrimitiveExpression(PrimitiveExpression primitiveExpression, object data)
        {
            StartNode(primitiveExpression);
            if (!string.IsNullOrEmpty(primitiveExpression.LiteralValue))
            {
                formatter.WriteToken(primitiveExpression.LiteralValue);
            }
            else
            {
                WritePrimitiveValue(primitiveExpression.Value);
            }
            return EndNode(primitiveExpression);
        }

        void WritePrimitiveValue(object val)
        {
            if (val == null)
            {
                // usually NullReferenceExpression should be used for this, but we'll handle it anyways
                WriteKeyword("null");
                return;
            }

            if (val is bool)
            {
                if ((bool)val)
                {
                    WriteKeyword("true");
                }
                else
                {
                    WriteKeyword("false");
                }
                return;
            }

            if (val is string)
            {
                formatter.WriteToken("\"" + ConvertString(val.ToString()) + "\"");
                lastWritten = LastWritten.Other;
            }
            else if (val is char)
            {
                formatter.WriteToken("'" + ConvertCharLiteral((char)val) + "'");
                lastWritten = LastWritten.Other;
            }
            else if (val is decimal)
            {
                formatter.WriteToken(((decimal)val).ToString(NumberFormatInfo.InvariantInfo) + "m");
                lastWritten = LastWritten.Other;
            }
            else if (val is float)
            {
                float f = (float)val;
                if (float.IsInfinity(f) || float.IsNaN(f))
                {
                    // Strictly speaking, these aren't PrimitiveExpressions;
                    // but we still support writing these to make life easier for code generators.
                    WriteKeyword("float");
                    WriteToken(".", AstNode.Roles.Dot);
                    if (float.IsPositiveInfinity(f))
                        WriteIdentifier("PositiveInfinity");
                    else if (float.IsNegativeInfinity(f))
                        WriteIdentifier("NegativeInfinity");
                    else
                        WriteIdentifier("NaN");
                    return;
                }
                formatter.WriteToken(f.ToString("R", NumberFormatInfo.InvariantInfo) + "f");
                lastWritten = LastWritten.Other;
            }
            else if (val is double)
            {
                double f = (double)val;
                if (double.IsInfinity(f) || double.IsNaN(f))
                {
                    // Strictly speaking, these aren't PrimitiveExpressions;
                    // but we still support writing these to make life easier for code generators.
                    WriteKeyword("double");
                    WriteToken(".", AstNode.Roles.Dot);
                    if (double.IsPositiveInfinity(f))
                        WriteIdentifier("PositiveInfinity");
                    else if (double.IsNegativeInfinity(f))
                        WriteIdentifier("NegativeInfinity");
                    else
                        WriteIdentifier("NaN");
                    return;
                }
                string number = f.ToString("R", NumberFormatInfo.InvariantInfo);
                if (number.IndexOf('.') < 0 && number.IndexOf('E') < 0)
                    number += ".0";
                formatter.WriteToken(number);
                // needs space if identifier follows number; this avoids mistaking the following identifier as type suffix
                lastWritten = LastWritten.KeywordOrIdentifier;
            }
            else if (val is IFormattable)
            {
                StringBuilder b = new StringBuilder();
                //				if (primitiveExpression.LiteralFormat == LiteralFormat.HexadecimalNumber) {
                //					b.Append("0x");
                //					b.Append(((IFormattable)val).ToString("x", NumberFormatInfo.InvariantInfo));
                //				} else {
                b.Append(((IFormattable)val).ToString(null, NumberFormatInfo.InvariantInfo));
                //				}
                if (val is uint || val is ulong)
                {
                    b.Append("u");
                }
                if (val is long || val is ulong)
                {
                    b.Append("L");
                }
                formatter.WriteToken(b.ToString());
                // needs space if identifier follows number; this avoids mistaking the following identifier as type suffix
                lastWritten = LastWritten.KeywordOrIdentifier;
            }
            else
            {
                formatter.WriteToken(val.ToString());
                lastWritten = LastWritten.Other;
            }
        }

        static string ConvertCharLiteral(char ch)
        {
            if (ch == '\'')
                return "\\'";
            return ConvertChar(ch);
        }

        /// <summary>
        /// Gets the escape sequence for the specified character.
        /// </summary>
        /// <remarks>This method does not convert ' or ".</remarks>
        public static string ConvertChar(char ch)
        {
            switch (ch)
            {
                case '\\':
                    return "\\\\";
                case '\0':
                    return "\\0";
                case '\a':
                    return "\\a";
                case '\b':
                    return "\\b";
                case '\f':
                    return "\\f";
                case '\n':
                    return "\\n";
                case '\r':
                    return "\\r";
                case '\t':
                    return "\\t";
                case '\v':
                    return "\\v";
                default:
                    if (char.IsControl(ch) || char.IsSurrogate(ch) ||
                        // print all uncommon white spaces as numbers
                        (char.IsWhiteSpace(ch) && ch != ' '))
                    {
                        return "\\u" + ((int)ch).ToString("x4");
                    }
                    else
                    {
                        return ch.ToString();
                    }
            }
        }

        /// <summary>
        /// Converts special characters to escape sequences within the given string.
        /// </summary>
        public static string ConvertString(string str)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in str)
            {
                if (ch == '"')
                    sb.Append("\\\"");
                else
                    sb.Append(ConvertChar(ch));
            }
            return sb.ToString();
        }

        #endregion

        public object VisitSizeOfExpression(SizeOfExpression sizeOfExpression, object data)
        {
            StartNode(sizeOfExpression);

            WriteKeyword("sizeof");
            LPar();
            Space(policy.SpacesWithinSizeOfParentheses);
            sizeOfExpression.Type.AcceptVisitor(this, data);
            Space(policy.SpacesWithinSizeOfParentheses);
            RPar();

            return EndNode(sizeOfExpression);
        }

        public object VisitStackAllocExpression(StackAllocExpression stackAllocExpression, object data)
        {
            StartNode(stackAllocExpression);
            WriteKeyword("stackalloc");
            stackAllocExpression.Type.AcceptVisitor(this, data);
            WriteCommaSeparatedListInBrackets(new[] { stackAllocExpression.CountExpression });
            return EndNode(stackAllocExpression);
        }

        public object VisitThisReferenceExpression(ThisReferenceExpression thisReferenceExpression, object data)
        {
            StartNode(thisReferenceExpression);
            WriteKeyword("this");
            return EndNode(thisReferenceExpression);
        }

        public object VisitTypeOfExpression(TypeOfExpression typeOfExpression, object data)
        {
            StartNode(typeOfExpression);

            WriteKeyword("typeof");
            LPar();
            Space(policy.SpacesWithinTypeOfParentheses);
            typeOfExpression.Type.AcceptVisitor(this, data);
            Space(policy.SpacesWithinTypeOfParentheses);
            RPar();

            return EndNode(typeOfExpression);
        }

        public object VisitTypeReferenceExpression(TypeReferenceExpression typeReferenceExpression, object data)
        {
            StartNode(typeReferenceExpression);
            typeReferenceExpression.Type.AcceptVisitor(this, data);
            return EndNode(typeReferenceExpression);
        }

        public object VisitUnaryOperatorExpression(UnaryOperatorExpression unaryOperatorExpression, object data)
        {
            StartNode(unaryOperatorExpression);
            UnaryOperatorType opType = unaryOperatorExpression.Operator;
            string opSymbol = UnaryOperatorExpression.GetOperatorSymbol(opType);
            if (opType == UnaryOperatorType.Await)
            {
                WriteKeyword(opSymbol, UnaryOperatorExpression.OperatorRole);
            }
            else if (!(opType == UnaryOperatorType.PostIncrement || opType == UnaryOperatorType.PostDecrement))
            {
                WriteToken(opSymbol, UnaryOperatorExpression.OperatorRole);
            }
            unaryOperatorExpression.Expression.AcceptVisitor(this, data);
            if (opType == UnaryOperatorType.PostIncrement || opType == UnaryOperatorType.PostDecrement)
                WriteToken(opSymbol, UnaryOperatorExpression.OperatorRole);
            return EndNode(unaryOperatorExpression);
        }

        public object VisitUncheckedExpression(UncheckedExpression uncheckedExpression, object data)
        {
            StartNode(uncheckedExpression);
            WriteKeyword("unchecked");
            LPar();
            Space(policy.SpacesWithinCheckedExpressionParantheses);
            uncheckedExpression.Expression.AcceptVisitor(this, data);
            Space(policy.SpacesWithinCheckedExpressionParantheses);
            RPar();
            return EndNode(uncheckedExpression);
        }

        #endregion

        #region Query Expressions
        public object VisitQueryExpression(QueryExpression queryExpression, object data)
        {
            StartNode(queryExpression);
            bool indent = !(queryExpression.Parent is QueryContinuationClause);
            if (indent)
            {
                formatter.Indent();
                NewLine();
            }
            bool first = true;
            foreach (var clause in queryExpression.Clauses)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    if (!(clause is QueryContinuationClause))
                        NewLine();
                }
                clause.AcceptVisitor(this, data);
            }
            if (indent)
                formatter.Unindent();
            return EndNode(queryExpression);
        }

        public object VisitQueryContinuationClause(QueryContinuationClause queryContinuationClause, object data)
        {
            StartNode(queryContinuationClause);
            queryContinuationClause.PrecedingQuery.AcceptVisitor(this, data);
            Space();
            WriteKeyword("into", QueryContinuationClause.IntoKeywordRole);
            Space();
            WriteIdentifier(queryContinuationClause.Identifier);
            return EndNode(queryContinuationClause);
        }

        public object VisitQueryFromClause(QueryFromClause queryFromClause, object data)
        {
            throw new NotImplementedException();
            //StartNode(queryFromClause);
            //WriteKeyword("from", QueryFromClause.FromKeywordRole);
            //queryFromClause.Type.AcceptVisitor(this, data);
            //Space();
            //WriteIdentifier(queryFromClause.Identifier);
            //Space();
            //WriteKeyword("in", QueryFromClause.InKeywordRole);
            //Space();
            //queryFromClause.Expression.AcceptVisitor(this, data);
            //return EndNode(queryFromClause);
        }

        public object VisitQueryLetClause(QueryLetClause queryLetClause, object data)
        {
            StartNode(queryLetClause);
            WriteKeyword("let");
            Space();
            WriteIdentifier(queryLetClause.Identifier);
            Space(policy.SpaceAroundAssignment);
            WriteToken("=", QueryLetClause.Roles.Assign);
            Space(policy.SpaceAroundAssignment);
            queryLetClause.Expression.AcceptVisitor(this, data);
            return EndNode(queryLetClause);
        }

        public object VisitQueryWhereClause(QueryWhereClause queryWhereClause, object data)
        {
            StartNode(queryWhereClause);
            WriteKeyword("where");
            Space();
            queryWhereClause.Condition.AcceptVisitor(this, data);
            return EndNode(queryWhereClause);
        }

        public object VisitQueryJoinClause(QueryJoinClause queryJoinClause, object data)
        {
            StartNode(queryJoinClause);
            WriteKeyword("join", QueryJoinClause.JoinKeywordRole);
            queryJoinClause.Type.AcceptVisitor(this, data);
            Space();
            WriteIdentifier(queryJoinClause.JoinIdentifier, QueryJoinClause.JoinIdentifierRole);
            Space();
            WriteKeyword("in", QueryJoinClause.InKeywordRole);
            Space();
            queryJoinClause.InExpression.AcceptVisitor(this, data);
            Space();
            WriteKeyword("on", QueryJoinClause.OnKeywordRole);
            Space();
            queryJoinClause.OnExpression.AcceptVisitor(this, data);
            Space();
            WriteKeyword("equals", QueryJoinClause.EqualsKeywordRole);
            Space();
            queryJoinClause.EqualsExpression.AcceptVisitor(this, data);
            if (queryJoinClause.IsGroupJoin)
            {
                Space();
                WriteKeyword("into", QueryJoinClause.IntoKeywordRole);
                WriteIdentifier(queryJoinClause.IntoIdentifier, QueryJoinClause.IntoIdentifierRole);
            }
            return EndNode(queryJoinClause);
        }

        public object VisitQueryOrderClause(QueryOrderClause queryOrderClause, object data)
        {
            StartNode(queryOrderClause);
            WriteKeyword("orderby");
            Space();
            WriteCommaSeparatedList(queryOrderClause.Orderings);
            return EndNode(queryOrderClause);
        }

        public object VisitQueryOrdering(QueryOrdering queryOrdering, object data)
        {
            StartNode(queryOrdering);
            queryOrdering.Expression.AcceptVisitor(this, data);
            switch (queryOrdering.Direction)
            {
                case QueryOrderingDirection.Ascending:
                    Space();
                    WriteKeyword("ascending");
                    break;
                case QueryOrderingDirection.Descending:
                    Space();
                    WriteKeyword("descending");
                    break;
            }
            return EndNode(queryOrdering);
        }

        public object VisitQuerySelectClause(QuerySelectClause querySelectClause, object data)
        {
            StartNode(querySelectClause);
            WriteKeyword("select");
            Space();
            querySelectClause.Expression.AcceptVisitor(this, data);
            return EndNode(querySelectClause);
        }

        public object VisitQueryGroupClause(QueryGroupClause queryGroupClause, object data)
        {
            StartNode(queryGroupClause);
            WriteKeyword("group", QueryGroupClause.GroupKeywordRole);
            Space();
            queryGroupClause.Projection.AcceptVisitor(this, data);
            Space();
            WriteKeyword("by", QueryGroupClause.ByKeywordRole);
            Space();
            queryGroupClause.Key.AcceptVisitor(this, data);
            return EndNode(queryGroupClause);
        }

        #endregion

        #region GeneralScope
        public object VisitAttribute(Attribute attribute, object data)
        {
            StartNode(attribute);
            attribute.Type.AcceptVisitor(this, data);
            if (attribute.Arguments.Count != 0 || !attribute.GetChildByRole(AstNode.Roles.LPar).IsNull)
            {
                Space(policy.SpaceBeforeMethodCallParentheses);
                WriteCommaSeparatedListInParenthesis(attribute.Arguments, policy.SpaceWithinMethodCallParentheses);
            }
            return EndNode(attribute);
        }

        public object VisitAttributeSection(AttributeSection attributeSection, object data)
        {
            StartNode(attributeSection);
            WriteToken("[", AstNode.Roles.LBracket);
            if (!string.IsNullOrEmpty(attributeSection.AttributeTarget))
            {
                WriteToken(attributeSection.AttributeTarget, AttributeSection.TargetRole);
                WriteToken(":", AttributeSection.Roles.Colon);
                Space();
            }
            WriteCommaSeparatedList(attributeSection.Attributes);
            WriteToken("]", AstNode.Roles.RBracket);
            if (attributeSection.Parent is ParameterDeclaration || attributeSection.Parent is TypeParameterDeclaration)
                Space();
            else
                NewLine();
            return EndNode(attributeSection);
        }

        public object VisitDelegateDeclaration(DelegateDeclaration delegateDeclaration, object data)
        {
            StartNode(delegateDeclaration);
            WriteAttributes(delegateDeclaration.Attributes);
            WriteModifiers(delegateDeclaration.ModifierTokens);
            WriteKeyword("delegate");
            delegateDeclaration.ReturnType.AcceptVisitor(this, data);
            Space();
            WriteIdentifier(delegateDeclaration.Name);
            WriteTypeParameters(delegateDeclaration.TypeParameters);
            Space(policy.SpaceBeforeDelegateDeclarationParentheses);
            WriteCommaSeparatedListInParenthesis(delegateDeclaration.Parameters, policy.SpaceWithinMethodDeclarationParentheses);
            //foreach (Constraint constraint in delegateDeclaration.Constraints)
            //{
            //    constraint.AcceptVisitor(this, data);
            //}
            Semicolon();
            return EndNode(delegateDeclaration);
        }

        public object VisitNamespaceDeclaration(NamespaceDeclaration namespaceDeclaration, object data)
        {
            StartNode(namespaceDeclaration);
            //TODO: What if there are different namespaces declarations involving the same type ??
            currNamespaces = new List<string>();
            currNamespaces.AddRange(namespaceDeclaration.Name.Split('.'));
            foreach (var member in namespaceDeclaration.Members)
                member.AcceptVisitor(this, data);
            return EndNode(namespaceDeclaration);
        }

        private void PatchMethodToBaseTemplate(TypeDeclaration typeDeclaration)
        {
            foreach (var member in typeDeclaration.Members)
            {
                if (member is MethodDeclaration)
                {
                    var method = member as MethodDeclaration;

                    if (IsGenericTemplateType(method.ReturnType))
                        method.ReturnType = new PtrType(new SimpleType("Object"));

                    foreach (ParameterDeclaration p in method.Parameters)
                    {
                        if (IsGenericTemplateType(p.Type))
                            p.Type = new PtrType(new SimpleType("Object"));
                    }
                }

                if (member is ConstructorDeclaration)
                {
                    var method = member as ConstructorDeclaration;

                    foreach (ParameterDeclaration p in method.Parameters)
                    {
                        if (IsGenericTemplateType(p.Type))
                            p.Type = new PtrType(new SimpleType("Object"));
                    }
                }

                if (member is FieldDeclaration)
                {
                    var field = member as FieldDeclaration;

                    if (IsGenericTemplateType(field.ReturnType))
                        field.ReturnType = new PtrType(new SimpleType("Object"));
                }
            }
        }

        public object VisitTypeDeclaration(TypeDeclaration typeDeclaration, object data)
        {
            //WRITE FIRST CPP AND THEN .H
            StartNode(typeDeclaration);
            if (typeDeclaration.TypeParameters.Any())
            {
                isGenericTemplate = true;
                TypeDeclaration orig = (TypeDeclaration)typeDeclaration.Clone();
                PatchMethodToBaseTemplate(typeDeclaration);
                TypeDeclarationCPP(typeDeclaration, data);
                TypeDeclarationTemplatesHeader(orig, data);
            }
            else
            {
                isGenericTemplate = false;
                if (typeDeclaration.ClassType != ClassType.Enum)
                    TypeDeclarationCPP(typeDeclaration, data);

                TypeDeclarationHeader(typeDeclaration, data);
            }
            return EndNode(typeDeclaration);
        }

        private void UsingNamespaces()
        {
            //WRITE NAMESPACES
            foreach (string s in Resolver.GetNeededNamespaces())
            {
                string tmp = "";
                if (currNamespaces != null)
                {
                    foreach (string _s in currNamespaces)
                        tmp += _s + ":";
                }

                if (s == tmp.TrimEnd(':') || String.IsNullOrEmpty(s))
                    continue;

                WriteKeyword("using");
                WriteKeyword("namespace");
                WriteIdentifier(s, IncludeDeclaration.Roles.Identifier);
                Semicolon();
            }
        }

        private void WriteNamespace()
        {
            if (currNamespaces != null)
            {
                foreach (string s in currNamespaces)
                {
                    WriteKeyword("namespace");
                    WriteIdentifier(s, IncludeDeclaration.Roles.Identifier);
                    OpenBrace(BraceStyle.EndOfLineWithoutSpace);
                }
            }
        }

        private void CloseNamespaceBraces()
        {
            if (currNamespaces != null)
                for (int i = 0; i < currNamespaces.Count; i++)
                {
                    CloseBrace(BraceStyle.NextLine);//END OF NAMESPACES
                    NewLine();
                }
        }

        private bool IsGenericTemplateType(AstType type)
        {
            string name = "";
            if (type is SimpleType)
            {
                name = (type as SimpleType).Identifier;
            }
            if (type is PtrType)
            {
                if ((type as PtrType).Target is SimpleType)
                    name = ((type as PtrType).Target as SimpleType).Identifier;
            }

            return Cache.GetExcluded().Contains(name);
        }

        private void WriteInlineMembers(AstNodeCollection<AttributedNode> members, string type)
        {
            foreach (var member in members)
            {
                if (member is MethodDeclaration)
                {
                    WriteAccesorModifier(member.ModifierTokens);
                    var methodDeclaration = member as MethodDeclaration;
                    StartNode(methodDeclaration);

                    WriteAttributes(methodDeclaration.Attributes);
                    //WriteAccesorModifier(methodDeclaration.ModifierTokens);
                    WriteKeyword("inline");
                    methodDeclaration.ReturnType.AcceptVisitor(this, null);
                    Space();

                    WritePrivateImplementationType(methodDeclaration.PrivateImplementationType);

                    methodDeclaration.NameToken.AcceptVisitor(this, null);
                    WriteTypeParameters(methodDeclaration.TypeParameters);
                    Space(policy.SpaceBeforeMethodDeclarationParentheses);
                    WriteCommaSeparatedListInParenthesis(methodDeclaration.Parameters, policy.SpaceWithinMethodDeclarationParentheses);

                    List<string> needsCast = new List<string>();
                    List<string> parametersName = new List<string>();
                    foreach (ParameterDeclaration p in methodDeclaration.Parameters)
                    {
                        parametersName.Add(p.Name);
                        if (IsGenericTemplateType(p.Type))
                            needsCast.Add(p.Name);

                    }

                    Expression[] parameters = new Expression[parametersName.Count];
                    int i = 0;
                    foreach (string s in parametersName)
                    {
                        if (needsCast.Contains(s))
                        {
                            parameters[i++] = new CastExpression(new PtrType(new SimpleType("Object")), new IdentifierExpression(s));
                        }
                        else
                        {
                            parameters[i++] = new IdentifierExpression(s);
                        }
                    }

                    //CALL SPECIALIZED OBJECT METHOD
                    BlockStatement blck = new BlockStatement();

                    if (IsGenericTemplateType(methodDeclaration.ReturnType))//NEEDS CAST
                    {
                        string tmpName = "var_tmp";
                        VariableDeclarationStatement varDeclStmt = new VariableDeclarationStatement(new PtrType(new SimpleType("Object")), tmpName,
                            new InvocationExpression(new MemberReferenceExpression(new TypeReferenceExpression(new SimpleType(type + "_Base")), methodDeclaration.Name), parameters));

                        blck.Add(varDeclStmt);

                        ReturnStatement rtstm = new ReturnStatement(new CastExpression((AstType)methodDeclaration.ReturnType.Clone(), new IdentifierExpression(tmpName)));
                        blck.Add(rtstm);
                    }
                    else
                    {
                        ReturnStatement rtstm = new ReturnStatement(new InvocationExpression(
                            new MemberReferenceExpression(
                                new TypeReferenceExpression(new SimpleType(type + "_Base")), methodDeclaration.Name), parameters));
                        blck.Add(rtstm);
                    }


                    WriteMethodBody(blck);
                    EndNode(methodDeclaration);
                }
            }
        }

        private void TypeDeclarationTemplatesHeader(TypeDeclaration typeDeclaration, object data)
        {
            //TODO: Se puede implementar con más claridad ?
            TypeDeclaration orig = (TypeDeclaration)typeDeclaration.Clone();
            PatchMethodToBaseTemplate(typeDeclaration);
            formatter.ChangeFile(typeDeclaration.Name + ".h");
            FileWritterManager.AddSourceFile(typeDeclaration.Name + ".h");

            WriteKeyword("#pragma", TypeDeclaration.Roles.Keyword);
            WriteKeyword("once", TypeDeclaration.Roles.Keyword);
            NewLine();

            //Write using declarations in header file
            foreach (AstNode n in headerNodes)
            {
                if (n is IncludeDeclaration)
                {
                    VisitIncludeDeclarationHeader(n as IncludeDeclaration, data);
                }
            }

            //WRITE RESOLVED TYPE DEPENDENCES
            foreach (string s in Resolver.GetTypeIncludes())
            {
                WriteKeyword("#include");
                WriteIdentifier(s);
                NewLine();
            }
            NewLine();

            UsingNamespaces();

            Resolver.Restart();

            WriteNamespace();

            WriteAttributes(typeDeclaration.Attributes);
            //WriteModifiers(typeDeclaration.ModifierTokens);
            BraceStyle braceStyle;
            switch (typeDeclaration.ClassType)
            {
                case ClassType.Enum:
                    WriteKeyword("enum");
                    braceStyle = policy.EnumBraceStyle;
                    break;
                case ClassType.Interface:
                    WriteKeyword("interface");
                    braceStyle = policy.InterfaceBraceStyle;
                    break;
                case ClassType.Struct:
                    WriteKeyword("struct");
                    braceStyle = policy.StructBraceStyle;
                    break;
                default:
                    WriteKeyword("class");
                    braceStyle = policy.ClassBraceStyle;
                    break;
            }
            WriteIdentifier(typeDeclaration.Name + "_Base");

            Space();
            WriteToken(":", TypeDeclaration.ColonRole);
            Space();
            //ÑAPA se añade virtual modifier y se quita
            var modif = new CppModifierToken(TextLocation.Empty, Modifiers.Virtual);
            typeDeclaration.ModifierTokens.Add(modif);
            WriteCommaSeparatedListWithModifiers(typeDeclaration.BaseTypes, typeDeclaration.ModifierTokens);
            typeDeclaration.ModifierTokens.Remove(modif);

            OpenBrace(braceStyle);

            if (typeDeclaration.ClassType == ClassType.Enum)
            {
                bool first = true;
                foreach (var member in typeDeclaration.Members)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        Comma(member, noSpaceAfterComma: true);
                        NewLine();
                    }
                    member.AcceptVisitor(this, data);
                }
                //OptionalComma();
                NewLine();
            }
            else
            {
                foreach (AstNode n in headerNodes)
                {
                    //Write Fields and methods with its Accessors:
                    if (n is MethodDeclaration)
                        VisitMethodDeclarationHeader(n as MethodDeclaration, data);
                    else if (n is ConstructorDeclaration)
                        VisitConstructorDeclarationHeader(n as ConstructorDeclaration, data);
                    else if (n is DestructorDeclaration)
                        VisitDestructorDeclarationHeader(n as DestructorDeclaration, data);
                    else if (n is FieldDeclaration)
                        VisitFieldDeclarationHeader(n as FieldDeclaration, data);
                }
            }
            CloseBrace(braceStyle);//END OF TYPE
            Semicolon();
            headerNodes.Clear();


            //After defining _Base class header, we can define the class template
            //We retrieve the original typeDeclaration
            typeDeclaration = (TypeDeclaration)orig.Clone();
            NewLine();

            //Write first the template<typename T> with inline methods
            #region <template typenameT>

            WriteAttributes(typeDeclaration.Attributes);

            WriteTypeParameters(typeDeclaration.TypeParameters, true);
            // HERE GOES THE TEMPLATE !
            BraceStyle braceStyle2;
            switch (typeDeclaration.ClassType)
            {
                case ClassType.Enum:
                    WriteKeyword("enum");
                    braceStyle2 = policy.EnumBraceStyle;
                    break;
                case ClassType.Interface:
                    WriteKeyword("interface");
                    braceStyle2 = policy.InterfaceBraceStyle;
                    break;
                case ClassType.Struct:
                    WriteKeyword("struct");
                    braceStyle2 = policy.StructBraceStyle;
                    break;
                default:
                    WriteKeyword("class");
                    braceStyle2 = policy.ClassBraceStyle;
                    break;
            }
            WriteIdentifier(typeDeclaration.Name);

            Space();
            WriteToken(":", TypeDeclaration.ColonRole);
            Space();

            //ÑAPA se añade virtual modifier y se quita
            var modif2 = new CppModifierToken(TextLocation.Empty, Modifiers.Virtual);
            typeDeclaration.ModifierTokens.Add(modif2);
            typeDeclaration.BaseTypes.Add(new SimpleType(typeDeclaration.Name + "_Base"));
            WriteCommaSeparatedListWithModifiers(typeDeclaration.BaseTypes, typeDeclaration.ModifierTokens);
            typeDeclaration.ModifierTokens.Remove(modif);
            OpenBrace(braceStyle);

            if (typeDeclaration.ClassType == ClassType.Enum)
            {
                bool first = true;
                foreach (var member in typeDeclaration.Members)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        Comma(member, noSpaceAfterComma: true);
                        NewLine();
                    }
                    member.AcceptVisitor(this, data);
                }
                //OptionalComma();
                NewLine();
            }
            else
            {
                WriteInlineMembers(typeDeclaration.Members, typeDeclaration.Name);
            }
            CloseBrace(braceStyle);//END OF TYPE
            Semicolon();
            #endregion

            CloseNamespaceBraces();

            formatter.ChangeFile("tmp");
        }

        private void TypeDeclarationCPP(TypeDeclaration typeDeclaration, object data)
        {
            formatter.ChangeFile(typeDeclaration.Name + ".cpp");
            FileWritterManager.AddSourceFile(typeDeclaration.Name + ".cpp");

            WriteKeyword("#include");
            Space();
            WriteIdentifier("\"" + typeDeclaration.Name + ".h\"", TypeDeclaration.Roles.Identifier);
            NewLine();

            WriteNamespace();

            foreach (var member in typeDeclaration.Members)
                member.AcceptVisitor(this, data);

            NewLine();
            CloseNamespaceBraces();
        }

        private void TypeDeclarationHeader(TypeDeclaration typeDeclaration, object data)
        {
            formatter.ChangeFile(typeDeclaration.Name + ".h");
            FileWritterManager.AddSourceFile(typeDeclaration.Name + ".h");

            WriteKeyword("#pragma", TypeDeclaration.Roles.Keyword);
            WriteKeyword("once", TypeDeclaration.Roles.Keyword);
            NewLine();

            //Write using declarations in header file
            foreach (AstNode n in headerNodes)
            {
                if (n is IncludeDeclaration)
                {
                    VisitIncludeDeclarationHeader(n as IncludeDeclaration, data);
                }
            }

            //WRITE RESOLVED TYPE DEPENDENCES
            foreach (string s in Resolver.GetTypeIncludes())
            {
                WriteKeyword("#include");
                WriteIdentifier(s);
                NewLine();
            }
            NewLine();

            UsingNamespaces();

            Resolver.Restart();

            WriteNamespace();

            string type2 = String.Empty;
            if (Resolver.NeedsForwardDeclaration(typeDeclaration.Name, out type2))
                WriteForwardDeclaration(type2);

            WriteAttributes(typeDeclaration.Attributes);
            //WriteModifiers(typeDeclaration.ModifierTokens);
            BraceStyle braceStyle;
            switch (typeDeclaration.ClassType)
            {
                case ClassType.Enum:
                    WriteKeyword("enum");
                    braceStyle = policy.EnumBraceStyle;
                    break;
                case ClassType.Interface:
                    WriteKeyword("interface");
                    braceStyle = policy.InterfaceBraceStyle;
                    break;
                case ClassType.Struct:
                    WriteKeyword("struct");
                    braceStyle = policy.StructBraceStyle;
                    break;
                default:
                    WriteKeyword("class");
                    braceStyle = policy.ClassBraceStyle;
                    break;
            }
            WriteIdentifier(typeDeclaration.Name);
            WriteTypeParameters(typeDeclaration.TypeParameters);

            Space();
            WriteToken(":", TypeDeclaration.ColonRole);
            Space();
            //ÑAPA se añade virtual modifier y se quita
            var modif = new CppModifierToken(TextLocation.Empty, Modifiers.Virtual);
            typeDeclaration.ModifierTokens.Add(modif);
            WriteCommaSeparatedListWithModifiers(typeDeclaration.BaseTypes, typeDeclaration.ModifierTokens);
            typeDeclaration.ModifierTokens.Remove(modif);

            OpenBrace(braceStyle);

            if (typeDeclaration.ClassType == ClassType.Enum)
            {
                bool first = true;
                foreach (var member in typeDeclaration.Members)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        Comma(member, noSpaceAfterComma: true);
                        NewLine();
                    }
                    member.AcceptVisitor(this, data);
                }
                //OptionalComma();
                NewLine();
            }
            else
            {
                foreach (AstNode n in headerNodes)
                {
                    //Write Fields and methods with its Accessors:
                    if (n is MethodDeclaration)
                        VisitMethodDeclarationHeader(n as MethodDeclaration, data);
                    else if (n is ConstructorDeclaration)
                        VisitConstructorDeclarationHeader(n as ConstructorDeclaration, data);
                    else if (n is DestructorDeclaration)
                        VisitDestructorDeclarationHeader(n as DestructorDeclaration, data);
                    else if (n is FieldDeclaration)
                        VisitFieldDeclarationHeader(n as FieldDeclaration, data);
                }
            }
            CloseBrace(braceStyle);//END OF TYPE
            Semicolon();
            CloseNamespaceBraces();
            headerNodes.Clear();

            formatter.ChangeFile("tmp");
        }

        private void WriteForwardDeclaration(string forwardDeclaration)
        {
            formatter.WriteComment(CommentType.SingleLine, "Forward Declaration");
            WriteKeyword("class");
            WriteIdentifier(forwardDeclaration, AstNode.Roles.Identifier);
            Semicolon();
            NewLine();
        }

        public object VisitIncludeDeclaration(IncludeDeclaration includeDeclaration, object data)
        {
            StartNode(includeDeclaration);
            headerNodes.Add(includeDeclaration);
            return EndNode(includeDeclaration);
        }

        private object VisitIncludeDeclarationHeader(IncludeDeclaration includeDeclaration, object data)
        {
            //TODO If the user has implemented a qualified namespace ?
            if (!(includeDeclaration.Import is QualifiedType))
            {
                //StartNode(usingDeclaration);
                WriteKeyword("#include");
                Space();

                includeDeclaration.Import.AcceptVisitor(this, data);
                NewLine();
            }
            //return EndNode(usingDeclaration);
            return null;
        }

        public object VisitExternAliasDeclaration(ExternAliasDeclaration externAliasDeclaration, object data)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region TypeMembers
        public object VisitAccessor(Accessor accessor, object data)
        {
            StartNode(accessor);
            WriteAttributes(accessor.Attributes);
            WriteModifiers(accessor.ModifierTokens);
            if (accessor.Role == PropertyDeclaration.GetterRole)
            {
                WriteKeyword("get");
            }
            else if (accessor.Role == PropertyDeclaration.SetterRole)
            {
                WriteKeyword("set");
            }
            else if (accessor.Role == CustomEventDeclaration.AddAccessorRole)
            {
                WriteKeyword("add");
            }
            else if (accessor.Role == CustomEventDeclaration.RemoveAccessorRole)
            {
                WriteKeyword("remove");
            }
            WriteMethodBody(accessor.Body);
            return EndNode(accessor);
        }

        public object VisitConstructorDeclaration(ConstructorDeclaration constructorDeclaration, object data)
        {
            StartNode(constructorDeclaration);
            WriteAttributes(constructorDeclaration.Attributes);
            //WriteAccesorModifier(constructorDeclaration.ModifierTokens);
            TypeDeclaration type = constructorDeclaration.Parent as TypeDeclaration;

            WriteIdentifier(type != null ? type.Name : constructorDeclaration.Name);
            WriteToken("::", MethodDeclaration.Roles.Dot);

            WriteIdentifier(type != null ? type.Name : constructorDeclaration.Name);
            Space(policy.SpaceBeforeConstructorDeclarationParentheses);
            WriteCommaSeparatedListInParenthesis(constructorDeclaration.Parameters, policy.SpaceWithinMethodDeclarationParentheses);
            if (!constructorDeclaration.Initializer.IsNull)
            {
                Space();
                constructorDeclaration.Initializer.AcceptVisitor(this, data);
            }
            WriteMethodBody(constructorDeclaration.Body);
            headerNodes.Add(constructorDeclaration);
            return EndNode(constructorDeclaration);
        }

        public object VisitConstructorDeclarationHeader(ConstructorDeclaration constructorDeclaration, object data)
        {
            //StartNode(constructorDeclaration);
            WriteAttributes(constructorDeclaration.Attributes);
            WriteAccesorModifier(constructorDeclaration.ModifierTokens);
            formatter.Indent();
            TypeDeclaration type = constructorDeclaration.Parent as TypeDeclaration;
            WriteIdentifier(type != null ? type.Name : constructorDeclaration.Name);
            Space(policy.SpaceBeforeConstructorDeclarationParentheses);
            WriteCommaSeparatedListInParenthesis(constructorDeclaration.Parameters, policy.SpaceWithinMethodDeclarationParentheses);
            if (!constructorDeclaration.Initializer.IsNull)
            {
                Space();
                constructorDeclaration.Initializer.AcceptVisitor(this, data);
            }
            //WriteMethodBody(constructorDeclaration.Body);
            Semicolon();
            //return EndNode(constructorDeclaration);
            formatter.Unindent();
            return null;
        }

        public object VisitConstructorInitializer(ConstructorInitializer constructorInitializer, object data)
        {
            StartNode(constructorInitializer);
            WriteToken(":", ConstructorInitializer.Roles.Colon);
            Space();
            if (constructorInitializer.ConstructorInitializerType == ConstructorInitializerType.This)
            {
                WriteKeyword("this");
            }
            else
            {
                WriteKeyword("base");
            }
            Space(policy.SpaceBeforeMethodCallParentheses);
            WriteCommaSeparatedListInParenthesis(constructorInitializer.Arguments, policy.SpaceWithinMethodCallParentheses);
            return EndNode(constructorInitializer);
        }

        public object VisitDestructorDeclaration(DestructorDeclaration destructorDeclaration, object data)
        {
            StartNode(destructorDeclaration);
            WriteAttributes(destructorDeclaration.Attributes);
            //WriteAccesorModifier(destructorDeclaration.ModifierTokens);
            TypeDeclaration type = destructorDeclaration.Parent as TypeDeclaration;

            WriteIdentifier(type != null ? type.Name : destructorDeclaration.Name);
            WriteToken("::", MethodDeclaration.Roles.Dot);

            WriteToken("~", DestructorDeclaration.TildeRole);
            WriteIdentifier(type != null ? type.Name : destructorDeclaration.Name);
            Space(policy.SpaceBeforeConstructorDeclarationParentheses);
            LPar();
            RPar();
            WriteMethodBody(destructorDeclaration.Body);
            headerNodes.Add(destructorDeclaration);
            return EndNode(destructorDeclaration);
        }

        public object VisitDestructorDeclarationHeader(DestructorDeclaration destructorDeclaration, object data)
        {
            //StartNode(destructorDeclaration);
            WriteAttributes(destructorDeclaration.Attributes);

            //<ÑAPA>
            //WriteAccesorModifier(destructorDeclaration.ModifierTokens);
            WriteKeyword("public:");
            NewLine();
            //</ÑAPA>

            formatter.Indent();
            WriteToken("~", DestructorDeclaration.TildeRole);
            TypeDeclaration type = destructorDeclaration.Parent as TypeDeclaration;
            WriteIdentifier(type != null ? type.Name : destructorDeclaration.Name);
            Space(policy.SpaceBeforeConstructorDeclarationParentheses);
            LPar();
            RPar();
            //WriteMethodBody(destructorDeclaration.Body);
            Semicolon();
            //return EndNode(destructorDeclaration);
            formatter.Unindent();
            return null;
        }

        public object VisitEnumMemberDeclaration(EnumMemberDeclaration enumMemberDeclaration, object data)
        {
            StartNode(enumMemberDeclaration);
            WriteAttributes(enumMemberDeclaration.Attributes);
            WriteModifiers(enumMemberDeclaration.ModifierTokens);
            WriteIdentifier(enumMemberDeclaration.Name);
            if (!enumMemberDeclaration.Initializer.IsNull)
            {
                Space(policy.SpaceAroundAssignment);
                WriteToken("=", EnumMemberDeclaration.Roles.Assign);
                Space(policy.SpaceAroundAssignment);
                enumMemberDeclaration.Initializer.AcceptVisitor(this, data);
            }
            return EndNode(enumMemberDeclaration);
        }

        public object VisitEventDeclaration(EventDeclaration eventDeclaration, object data)
        {
            StartNode(eventDeclaration);
            WriteAttributes(eventDeclaration.Attributes);
            WriteModifiers(eventDeclaration.ModifierTokens);
            WriteKeyword("__event");
            eventDeclaration.ReturnType.AcceptVisitor(this, data);
            Space();
            WriteCommaSeparatedList(eventDeclaration.Variables);
            Semicolon();
            return EndNode(eventDeclaration);
        }

        public object VisitCustomEventDeclaration(CustomEventDeclaration customEventDeclaration, object data)
        {
            StartNode(customEventDeclaration);
            WriteAttributes(customEventDeclaration.Attributes);
            WriteModifiers(customEventDeclaration.ModifierTokens);
            WriteKeyword("__event");
            customEventDeclaration.ReturnType.AcceptVisitor(this, data);
            Space();
            WritePrivateImplementationType(customEventDeclaration.PrivateImplementationType);
            WriteIdentifier(customEventDeclaration.Name);
            OpenBrace(policy.EventBraceStyle);
            // output add/remove in their original order
            foreach (AstNode node in customEventDeclaration.Children)
            {
                if (node.Role == CustomEventDeclaration.AddAccessorRole || node.Role == CustomEventDeclaration.RemoveAccessorRole)
                {
                    node.AcceptVisitor(this, data);
                }
            }
            CloseBrace(policy.EventBraceStyle);
            NewLine();
            return EndNode(customEventDeclaration);
        }

        public object VisitFieldDeclaration(FieldDeclaration fieldDeclaration, object data)
        {
            StartNode(fieldDeclaration);

            if (fieldDeclaration.HasModifier(Modifiers.Static))
            {
                fieldDeclaration.ReturnType.AcceptVisitor(this, data);
                Space();

                TypeDeclaration tdecl = fieldDeclaration.Parent as TypeDeclaration;
                WriteIdentifier(tdecl != null ? tdecl.Name : String.Empty, MethodDeclaration.Roles.Identifier);
                WriteToken("::", MethodDeclaration.Roles.DoubleColon);

                WriteCommaSeparatedList(fieldDeclaration.Variables);
                Semicolon();

                //Reset the variable initializer befor add to header ndoes
                for (int i = 0; i < fieldDeclaration.Variables.Count; i++)
                {
                    VariableInitializer vi = fieldDeclaration.Variables.ElementAt(i);
                    fieldDeclaration.Variables.Remove(vi);
                    vi = new VariableInitializer(vi.Name);
                    fieldDeclaration.Variables.Add(vi);
                }
            }
            headerNodes.Add(fieldDeclaration);
            return EndNode(fieldDeclaration);
        }

        private object VisitFieldDeclarationHeader(FieldDeclaration fieldDeclaration, object data)
        {
            StartNode(fieldDeclaration);
            WriteAttributes(fieldDeclaration.Attributes);
            WriteAccesorModifier(fieldDeclaration.ModifierTokens);
            formatter.Indent();
            fieldDeclaration.ReturnType.AcceptVisitor(this, data);
            Space();
            WriteCommaSeparatedList(fieldDeclaration.Variables);
            Semicolon();
            formatter.Unindent();
            return EndNode(fieldDeclaration);
        }

        public object VisitFixedFieldDeclaration(FixedFieldDeclaration fixedFieldDeclaration, object data)
        {
            StartNode(fixedFieldDeclaration);
            WriteAttributes(fixedFieldDeclaration.Attributes);
            WriteModifiers(fixedFieldDeclaration.ModifierTokens);
            WriteKeyword("fixed");
            Space();
            fixedFieldDeclaration.ReturnType.AcceptVisitor(this, data);
            Space();
            WriteCommaSeparatedList(fixedFieldDeclaration.Variables);
            Semicolon();
            return EndNode(fixedFieldDeclaration);
        }

        public object VisitFixedVariableInitializer(FixedVariableInitializer fixedVariableInitializer, object data)
        {
            StartNode(fixedVariableInitializer);
            WriteIdentifier(fixedVariableInitializer.Name);
            fixedVariableInitializer.NameToken.AcceptVisitor(this, data);
            if (!fixedVariableInitializer.CountExpression.IsNull)
            {
                WriteToken("[", AstNode.Roles.LBracket);
                Space(policy.SpacesWithinBrackets);
                fixedVariableInitializer.CountExpression.AcceptVisitor(this, data);
                Space(policy.SpacesWithinBrackets);
                WriteToken("]", AstNode.Roles.RBracket);
            }
            return EndNode(fixedVariableInitializer);
        }

        public object VisitIndexerDeclaration(IndexerDeclaration indexerDeclaration, object data)
        {
            StartNode(indexerDeclaration);
            WriteAttributes(indexerDeclaration.Attributes);
            WriteModifiers(indexerDeclaration.ModifierTokens);
            indexerDeclaration.ReturnType.AcceptVisitor(this, data);
            WritePrivateImplementationType(indexerDeclaration.PrivateImplementationType);
            WriteKeyword("this");
            Space(policy.SpaceBeforeMethodDeclarationParentheses);
            WriteCommaSeparatedListInBrackets(indexerDeclaration.Parameters, policy.SpaceWithinMethodDeclarationParentheses);
            OpenBrace(policy.PropertyBraceStyle);
            // output get/set in their original order
            foreach (AstNode node in indexerDeclaration.Children)
            {
                if (node.Role == IndexerDeclaration.GetterRole || node.Role == IndexerDeclaration.SetterRole)
                {
                    node.AcceptVisitor(this, data);
                }
            }
            CloseBrace(policy.PropertyBraceStyle);
            NewLine();
            return EndNode(indexerDeclaration);
        }

        public object VisitMethodDeclaration(MethodDeclaration methodDeclaration, object data)
        {
            StartNode(methodDeclaration);
            WriteAttributes(methodDeclaration.Attributes);
            //WriteAccesorModifier(methodDeclaration.ModifierTokens);         
            methodDeclaration.ReturnType.AcceptVisitor(this, data);
            Space();

            WritePrivateImplementationType(methodDeclaration.PrivateImplementationType);

            //TODO: se podria implementar mejor ?
            Identifier tdecl = methodDeclaration.TypeMember;
            WriteIdentifier(tdecl != null ? (isGenericTemplate ? tdecl.Name + "_T_Base" : tdecl.Name) : String.Empty, MethodDeclaration.Roles.Identifier);
            WriteToken("::", MethodDeclaration.Roles.DoubleColon);

            methodDeclaration.NameToken.AcceptVisitor(this, data);
            WriteTypeParameters(methodDeclaration.TypeParameters);
            Space(policy.SpaceBeforeMethodDeclarationParentheses);
            WriteCommaSeparatedListInParenthesis(methodDeclaration.Parameters, policy.SpaceWithinMethodDeclarationParentheses);
            WriteMethodBody(methodDeclaration.Body);
            headerNodes.Add(methodDeclaration);
            return EndNode(methodDeclaration);
        }

        private object VisitMethodDeclarationHeader(MethodDeclaration methodDeclaration, object data)
        {
            //StartNode(methodDeclaration);
            WriteAttributes(methodDeclaration.Attributes);

            if (methodDeclaration.Name == "Main")
            {
                NamespaceDeclaration entryNamespace = methodDeclaration.Parent.Parent as NamespaceDeclaration;
                MainWritter.GenerateMain((methodDeclaration.Parent as TypeDeclaration).Name,
                    entryNamespace == null ? String.Empty : entryNamespace.Name, methodDeclaration.Parameters.Any());
                //<ÑAPA>
                //Force the Main to be public because it will be called from main.cpp and has to be accessible
                WriteKeyword("public:");
                NewLine();
                formatter.Indent();
                methodDeclaration.ModifierTokens.Remove(methodDeclaration.ModifierTokens.First());
                WriteModifiers(methodDeclaration.ModifierTokens);
                //</ÑAPA>                
            }
            else
            {
                WriteAccesorModifier(methodDeclaration.ModifierTokens);
                formatter.Indent();
            }

            methodDeclaration.ReturnType.AcceptVisitor(this, data);
            Space();

            WritePrivateImplementationType(methodDeclaration.PrivateImplementationType);

            WriteIdentifier(methodDeclaration.Name);
            WriteTypeParameters(methodDeclaration.TypeParameters);
            Space(policy.SpaceBeforeMethodDeclarationParentheses);
            WriteCommaSeparatedListInParenthesis(methodDeclaration.Parameters, policy.SpaceWithinMethodDeclarationParentheses);
            //foreach (Constraint constraint in methodDeclaration.Constraints)
            //{
            //    constraint.AcceptVisitor(this, data);
            //}
            //WriteMethodBody(methodDeclaration.Body);
            Semicolon();
            formatter.Unindent();
            //return EndNode(methodDeclaration);
            return null;
        }

        public object VisitOperatorDeclaration(OperatorDeclaration operatorDeclaration, object data)
        {
            StartNode(operatorDeclaration);
            WriteAttributes(operatorDeclaration.Attributes);
            WriteModifiers(operatorDeclaration.ModifierTokens);
            if (operatorDeclaration.OperatorType == OperatorType.Explicit)
            {
                WriteKeyword("explicit", OperatorDeclaration.OperatorTypeRole);
            }
            else if (operatorDeclaration.OperatorType == OperatorType.Implicit)
            {
                WriteKeyword("implicit", OperatorDeclaration.OperatorTypeRole);
            }
            else
            {
                operatorDeclaration.ReturnType.AcceptVisitor(this, data);
            }
            WriteKeyword("operator", OperatorDeclaration.OperatorKeywordRole);
            Space();
            if (operatorDeclaration.OperatorType == OperatorType.Explicit
                || operatorDeclaration.OperatorType == OperatorType.Implicit)
            {
                operatorDeclaration.ReturnType.AcceptVisitor(this, data);
            }
            else
            {
                WriteToken(OperatorDeclaration.GetToken(operatorDeclaration.OperatorType), OperatorDeclaration.OperatorTypeRole);
            }
            Space(policy.SpaceBeforeMethodDeclarationParentheses);
            WriteCommaSeparatedListInParenthesis(operatorDeclaration.Parameters, policy.SpaceWithinMethodDeclarationParentheses);
            WriteMethodBody(operatorDeclaration.Body);
            return EndNode(operatorDeclaration);
        }

        public object VisitParameterDeclaration(ParameterDeclaration parameterDeclaration, object data)
        {
            StartNode(parameterDeclaration);
            WriteAttributes(parameterDeclaration.Attributes);
            switch (parameterDeclaration.ParameterModifier)
            {
                case ParameterModifier.Ref:
                    //WriteKeyword("ref", ParameterDeclaration.ModifierRole);
                    WriteKeyword("*", ParameterDeclaration.ModifierRole);
                    break;
                case ParameterModifier.Out:
                    WriteKeyword("out", ParameterDeclaration.ModifierRole);
                    break;
                case ParameterModifier.Params:
                    WriteKeyword("params", ParameterDeclaration.ModifierRole);
                    break;
                case ParameterModifier.This:
                    WriteKeyword("this", ParameterDeclaration.ModifierRole);
                    break;
            }
            parameterDeclaration.Type.AcceptVisitor(this, data);
            if (!parameterDeclaration.Type.IsNull && !string.IsNullOrEmpty(parameterDeclaration.Name))
                Space();
            parameterDeclaration.NameToken.AcceptVisitor(this, data);

            if (!parameterDeclaration.DefaultExpression.IsNull)
            {
                Space(policy.SpaceAroundAssignment);
                WriteToken("=", ParameterDeclaration.Roles.Assign);
                Space(policy.SpaceAroundAssignment);
                parameterDeclaration.DefaultExpression.AcceptVisitor(this, data);
            }
            return EndNode(parameterDeclaration);
        }

        public object VisitPropertyDeclaration(PropertyDeclaration propertyDeclaration, object data)
        {
            StartNode(propertyDeclaration);
            foreach (AstNode node in propertyDeclaration.Children)
            {
                if (node.Role == IndexerDeclaration.GetterRole || node.Role == IndexerDeclaration.SetterRole)
                {
                    node.AcceptVisitor(this, data);
                }
            }
            return EndNode(propertyDeclaration);
        }

        #endregion

        #region Other nodes

        public object VisitVariableInitializer(VariableInitializer variableInitializer, object data)
        {
            StartNode(variableInitializer);
            variableInitializer.NameToken.AcceptVisitor(this, data);

            if (!variableInitializer.Initializer.IsNull)
            {
                Space(policy.SpaceAroundAssignment);
                WriteToken("=", VariableInitializer.Roles.Assign);

                Space(policy.SpaceAroundAssignment);
                variableInitializer.Initializer.AcceptVisitor(this, data);
            }
            return EndNode(variableInitializer);
        }

        public object VisitCompilationUnit(CompilationUnit compilationUnit, object data)
        {
            // don't do node tracking as we visit all children directly
            foreach (AstNode node in compilationUnit.Children)
                node.AcceptVisitor(this, data);
            return null;
        }

        public object VisitSimpleType(SimpleType simpleType, object data)
        {
            StartNode(simpleType);
            WriteIdentifier(simpleType.Identifier);
            WriteTypeArguments(simpleType.TypeArguments);
            return EndNode(simpleType);
        }

        public object VisitComposedType(ComposedType composedType, object data)
        {
            StartNode(composedType);
            composedType.BaseType.AcceptVisitor(this, data);
            if (composedType.HasNullableSpecifier)
                WriteToken("?", ComposedType.NullableRole);
            for (int i = 0; i < composedType.PointerRank; i++)
                WriteToken("*", ComposedType.PointerRole);
            return EndNode(composedType);
        }

        public object VisitArraySpecifier(ArraySpecifier arraySpecifier, object data)
        {
            StartNode(arraySpecifier);
            //C++: when a method returns an array it is declared like:  Type *myMethod(Type1 param1, Type2 param2[]); (also Type **myMethod(void); ...)
            if (arraySpecifier.Parent.Parent is MethodDeclaration)
            {
                formatter.WriteToken("*");
                foreach (var comma in arraySpecifier.GetChildrenByRole(ArraySpecifier.Roles.Comma))
                {
                    //WriteSpecialsUpToNode(comma);
                    formatter.WriteToken("*");
                    lastWritten = LastWritten.Other;
                }
                return EndNode(arraySpecifier);
            }

            WriteToken("[", ArraySpecifier.Roles.LBracket);
            foreach (var comma in arraySpecifier.GetChildrenByRole(ArraySpecifier.Roles.Comma))
            {
                WriteSpecialsUpToNode(comma);
                formatter.WriteToken(",");
                lastWritten = LastWritten.Other;
            }
            WriteToken("]", ArraySpecifier.Roles.RBracket);

            //if (arraySpecifier.Dimensions <= 0)
            //    return EndNode(arraySpecifier);
            //else if (arraySpecifier.Dimensions > 1)
            //{
            //    WriteToken("*", ArraySpecifier.Roles.LBracket);
            //    foreach (var comma in arraySpecifier.GetChildrenByRole(ArraySpecifier.Roles.Comma))
            //    {
            //        WriteSpecialsUpToNode(comma);
            //        formatter.WriteToken("*");
            //        lastWritten = LastWritten.Other;
            //    }
            //}
            //else
            //{
            //    WriteToken("[", ArraySpecifier.Roles.LBracket);
            //    WriteToken("]", ArraySpecifier.Roles.RBracket);
            //}


            return EndNode(arraySpecifier);
        }

        public object VisitPrimitiveType(PrimitiveType primitiveType, object data)
        {
            StartNode(primitiveType);
            WriteKeyword(primitiveType.Keyword);
            if (primitiveType.Keyword == "new")
            {
                // new() constraint
                LPar();
                RPar();
            }
            return EndNode(primitiveType);
        }

        public object VisitComment(Comment comment, object data)
        {
            if (lastWritten == LastWritten.Division)
            {
                // When there's a comment starting after a division operator
                // "1.0 / /*comment*/a", then we need to insert a space in front of the comment.
                formatter.Space();
            }
            formatter.StartNode(comment);
            formatter.WriteComment(comment.CommentType, comment.Content);
            formatter.EndNode(comment);
            lastWritten = LastWritten.Whitespace;
            return null;
        }

        public object VisitPreProcessorDirective(PreProcessorDirective preProcessorDirective, object data)
        {
            formatter.StartNode(preProcessorDirective);
            formatter.WritePreProcessorDirective(preProcessorDirective.Type, preProcessorDirective.Argument);
            formatter.EndNode(preProcessorDirective);
            lastWritten = LastWritten.Whitespace;
            return null;
        }

        public object VisitTypeParameterDeclaration(TypeParameterDeclaration typeParameterDeclaration, object data)
        {
            StartNode(typeParameterDeclaration);
            WriteAttributes(typeParameterDeclaration.Attributes);
            switch (typeParameterDeclaration.Variance)
            {
                case VarianceModifier.Invariant:
                    break;
                case VarianceModifier.Covariant:
                    WriteKeyword("out");
                    break;
                case VarianceModifier.Contravariant:
                    WriteKeyword("in");
                    break;
                default:
                    throw new NotSupportedException("Invalid value for VarianceModifier");
            }
            WriteIdentifier(typeParameterDeclaration.Name);
            return EndNode(typeParameterDeclaration);
        }

        //public object VisitConstraint(Constraint constraint, object data)
        //{
        //    StartNode(constraint);
        //    Space();
        //    WriteKeyword("where");
        //    WriteIdentifier(constraint.TypeParameter.Identifier);
        //    Space();
        //    WriteToken(":", Constraint.ColonRole);
        //    Space();
        //    WriteCommaSeparatedList(constraint.BaseTypes);
        //    return EndNode(constraint);
        //}

        public object VisitCSharpTokenNode(CppTokenNode cSharpTokenNode, object data)
        {
            CppModifierToken mod = cSharpTokenNode as CppModifierToken;
            if (mod != null)
            {
                StartNode(mod);
                //In c++ we change from internal to public !
                if (mod.Modifier == Modifiers.Internal)
                    WriteKeyword(CppModifierToken.GetModifierName(Modifiers.Public));
                else
                    WriteKeyword(CppModifierToken.GetModifierName(mod.Modifier));
                return EndNode(mod);
            }
            else
            {
                throw new NotSupportedException("Should never visit individual tokens");
            }
        }

        public object VisitIdentifier(Identifier identifier, object data)
        {
            StartNode(identifier);
            WriteIdentifier(identifier.Name);
            return EndNode(identifier);
        }

        #endregion

        #region Pattern Nodes
        public object VisitPatternPlaceholder(AstNode placeholder, PatternMatching.Pattern pattern, object data)
        {
            StartNode(placeholder);
            VisitNodeInPattern(pattern, data);
            return EndNode(placeholder);
        }

        void VisitAnyNode(AnyNode anyNode, object data)
        {
            if (!string.IsNullOrEmpty(anyNode.GroupName))
            {
                WriteIdentifier(anyNode.GroupName);
                WriteToken(":", AstNode.Roles.Colon);
            }
        }

        void VisitBackreference(Backreference backreference, object data)
        {
            WriteKeyword("backreference");
            LPar();
            WriteIdentifier(backreference.ReferencedGroupName);
            RPar();
        }

        void VisitIdentifierExpressionBackreference(IdentifierExpressionBackreference identifierExpressionBackreference, object data)
        {
            WriteKeyword("identifierBackreference");
            LPar();
            WriteIdentifier(identifierExpressionBackreference.ReferencedGroupName);
            RPar();
        }

        void VisitChoice(Choice choice, object data)
        {
            WriteKeyword("choice");
            Space();
            LPar();
            NewLine();
            formatter.Indent();
            foreach (INode alternative in choice)
            {
                VisitNodeInPattern(alternative, data);
                if (alternative != choice.Last())
                    WriteToken(",", AstNode.Roles.Comma);
                NewLine();
            }
            formatter.Unindent();
            RPar();
        }

        void VisitNamedNode(NamedNode namedNode, object data)
        {
            if (!string.IsNullOrEmpty(namedNode.GroupName))
            {
                WriteIdentifier(namedNode.GroupName);
                WriteToken(":", AstNode.Roles.Colon);
            }
            VisitNodeInPattern(namedNode.ChildNode, data);
        }

        void VisitRepeat(Repeat repeat, object data)
        {
            WriteKeyword("repeat");
            LPar();
            if (repeat.MinCount != 0 || repeat.MaxCount != int.MaxValue)
            {
                WriteIdentifier(repeat.MinCount.ToString());
                WriteToken(",", AstNode.Roles.Comma);
                WriteIdentifier(repeat.MaxCount.ToString());
                WriteToken(",", AstNode.Roles.Comma);
            }
            VisitNodeInPattern(repeat.ChildNode, data);
            RPar();
        }

        void VisitOptionalNode(OptionalNode optionalNode, object data)
        {
            WriteKeyword("optional");
            LPar();
            VisitNodeInPattern(optionalNode.ChildNode, data);
            RPar();
        }

        void VisitNodeInPattern(INode childNode, object data)
        {
            if (childNode is AstNode)
            {
                ((AstNode)childNode).AcceptVisitor(this, data);
            }
            else if (childNode is IdentifierExpressionBackreference)
            {
                VisitIdentifierExpressionBackreference((IdentifierExpressionBackreference)childNode, data);
            }
            else if (childNode is Choice)
            {
                VisitChoice((Choice)childNode, data);
            }
            else if (childNode is AnyNode)
            {
                VisitAnyNode((AnyNode)childNode, data);
            }
            else if (childNode is Backreference)
            {
                VisitBackreference((Backreference)childNode, data);
            }
            else if (childNode is NamedNode)
            {
                VisitNamedNode((NamedNode)childNode, data);
            }
            else if (childNode is OptionalNode)
            {
                VisitOptionalNode((OptionalNode)childNode, data);
            }
            else if (childNode is Repeat)
            {
                VisitRepeat((Repeat)childNode, data);
            }
            else
            {
                WritePrimitiveValue(childNode);
            }
        }
        #endregion

        #region IsKeyword Test
        static readonly HashSet<string> unconditionalKeywords = new HashSet<string> {
            "abstract", "base", "bool", "break", "byte", "case", "catch",
            "char", "checked", "class", "const", "continue", "decimal", "default", "delegate",
            "do", "double", "else", "enum", "event", "explicit", "extern", "false",
            "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
            "in", "int", "interface", "lock", "long", "namespace",
            "new", "null", "operator", "out", "override", "params", "private",
            "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
            "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "virtual", "void", "volatile", "while"
        };
        static readonly HashSet<string> queryKeywords = new HashSet<string> {
            "from", "where", "join", "on", "equals", "into", "let", "orderby",
            "ascending", "descending", "select", "group", "by"
        };

        /// <summary>
        /// Determines whether the specified identifier is a keyword in the given context.
        /// </summary>
        public static bool IsKeyword(string identifier, AstNode context)
        {
            if (unconditionalKeywords.Contains(identifier))
                return true;
            foreach (AstNode ancestor in context.Ancestors)
            {
                if (ancestor is QueryExpression && queryKeywords.Contains(identifier))
                    return true;
                if (identifier == "await")
                {
                    // with lambdas/anonymous methods,
                    if (ancestor is LambdaExpression)
                        return ((LambdaExpression)ancestor).IsAsync;
                    if (ancestor is AnonymousMethodExpression)
                        return ((AnonymousMethodExpression)ancestor).IsAsync;
                    if (ancestor is AttributedNode)
                        return (((AttributedNode)ancestor).Modifiers & Modifiers.Async) == Modifiers.Async;
                }
            }
            return false;
        }
        #endregion

        #region Write constructs
        void WriteTypeArguments(IEnumerable<AstType> typeArguments)
        {
            if (typeArguments.Any())
            {
                WriteToken("<", AstNode.Roles.LChevron);
                WriteCommaSeparatedList(typeArguments);
                WriteToken(">", AstNode.Roles.RChevron);
            }
        }

        public void WriteTypeParameters(IEnumerable<TypeParameterDeclaration> typeParameters, bool declaration = false, bool empty = false)
        {
            if (typeParameters.Any() || empty)
            {
                if (declaration)
                {
                    WriteKeyword("template");
                    WriteToken("<", AstNode.Roles.LChevron);
                    if (!empty)
                    {
                        WriteKeyword("typename");
                        WriteCommaSeparatedList(typeParameters);
                    }
                    WriteToken(">", AstNode.Roles.RChevron);
                    NewLine();
                }
                else
                {
                    WriteToken("<", AstNode.Roles.LChevron);
                    WriteCommaSeparatedList(typeParameters);
                    WriteToken(">", AstNode.Roles.RChevron);
                }
            }
        }

        void WriteModifiers(IEnumerable<CppModifierToken> modifierTokens)
        {
            foreach (CppModifierToken modifier in modifierTokens)
            {
                modifier.AcceptVisitor(this, null);
            }
        }

        void WriteAccesorModifier(IEnumerable<CppModifierToken> modifierTokens)
        {
            bool isFirst = true;
            if (!modifierTokens.Any())
            {
                WriteKeyword("private", CppModifierToken.Roles.Keyword);
                WriteToken(":", CppModifierToken.Roles.Colon);
                NewLine();
            }
            else
                foreach (CppModifierToken modifier in modifierTokens)
                {
                    modifier.AcceptVisitor(this, null);
                    if (isFirst)
                    {
                        isFirst = false;
                        WriteToken(":", CppModifierToken.Roles.Colon);
                        NewLine();
                    }
                }
            return;
        }

        void WriteQualifiedIdentifier(IEnumerable<Identifier> identifiers)
        {
            bool first = true;
            foreach (Identifier ident in identifiers)
            {
                if (first)
                {
                    first = false;
                    if (lastWritten == LastWritten.KeywordOrIdentifier)
                        formatter.Space();
                }
                else
                {
                    WriteSpecialsUpToRole(AstNode.Roles.Dot, ident);
                    formatter.WriteToken(".");
                    lastWritten = LastWritten.Other;
                }
                WriteSpecialsUpToNode(ident);
                formatter.WriteIdentifier(ident.Name);
                lastWritten = LastWritten.KeywordOrIdentifier;
            }
        }

        void WriteEmbeddedStatement(Statement embeddedStatement)
        {
            if (embeddedStatement.IsNull)
                return;
            BlockStatement block = embeddedStatement as BlockStatement;
            if (block != null)
                VisitBlockStatement(block, null);
            else
            {
                NewLine();
                formatter.Indent();
                embeddedStatement.AcceptVisitor(this, null);
                formatter.Unindent();
            }
        }

        void WriteMethodBody(BlockStatement body)
        {
            if (body.IsNull)
                Semicolon();
            else
                VisitBlockStatement(body, null);
        }

        void WriteAttributes(IEnumerable<AttributeSection> attributes)
        {
            foreach (AttributeSection attr in attributes)
            {
                formatter.WriteComment(CommentType.SingleLine, "Deleted attribute");
                continue;
                //attr.AcceptVisitor(this, null);
            }
        }

        void WritePrivateImplementationType(AstType privateImplementationType)
        {
            if (!privateImplementationType.IsNull)
            {
                privateImplementationType.AcceptVisitor(this, null);
                WriteToken("::", AstNode.Roles.Dot);
            }
        }

        #endregion

        #region Comma
        /// <summary>
        /// Writes a comma.
        /// </summary>
        /// <param name="nextNode">The next node after the comma.</param>
        /// <param name="noSpaceAfterComma">When set prevents printing a space after comma.</param>
        void Comma(AstNode nextNode, bool noSpaceAfterComma = false)
        {
            WriteSpecialsUpToRole(AstNode.Roles.Comma, nextNode);
            Space(policy.SpaceBeforeBracketComma); // TODO: Comma policy has changed.
            formatter.WriteToken(",");
            lastWritten = LastWritten.Other;
            Space(!noSpaceAfterComma && policy.SpaceAfterBracketComma); // TODO: Comma policy has changed.
        }

        /// <summary>
        /// Writes an optional comma, e.g. at the end of an enum declaration or in an array initializer
        /// </summary>
        //void OptionalComma()
        //{
        //    // Look if there's a comma after the current node, and insert it if it exists.
        //    AstNode pos = positionStack.Peek();
        //    while (pos != null && pos.NodeType == NodeType.Whitespace)
        //        pos = pos.NextSibling;
        //    if (pos != null && pos.Role == AstNode.Roles.Comma)
        //        Comma(null, noSpaceAfterComma: true);
        //}

        /// <summary>
        /// Writes an optional semicolon, e.g. at the end of a type or namespace declaration.
        /// </summary>
        //void OptionalSemicolon()
        //{
        //    // Look if there's a semicolon after the current node, and insert it if it exists.
        //    AstNode pos = positionStack.Peek();
        //    while (pos != null && pos.NodeType == NodeType.Whitespace)
        //        pos = pos.NextSibling;
        //    if (pos != null && pos.Role == AstNode.Roles.Semicolon)
        //        Semicolon();
        //}

        void WriteCommaSeparatedList(IEnumerable<AstNode> list)
        {
            bool isFirst = true;
            foreach (AstNode node in list)
            {
                if (isFirst)
                {
                    isFirst = false;
                }
                else
                {
                    Comma(node);
                }
                node.AcceptVisitor(this, null);
            }
        }

        void WriteCommaSeparatedListWithModifiers(IEnumerable<AstNode> list, IEnumerable<CppModifierToken> modifiers)
        {
            //ÑAPA Crear nodo BaseType con un Modifier, ahora mismo se pone siempre el modifier de la clase principal,
            //pero i.e. class List : public Object, public virtual gc_cleanup { no se puede !
            bool isFirst = true;
            foreach (AstNode node in list)
            {
                if (isFirst)
                {
                    isFirst = false;
                }
                else
                {
                    Comma(node);
                }
                WriteModifiers(modifiers);

                node.AcceptVisitor(this, null);
            }
        }

        void WriteCommaSeparatedListInParenthesis(IEnumerable<AstNode> list, bool spaceWithin)
        {
            LPar();
            if (list.Any())
            {
                Space(spaceWithin);
                WriteCommaSeparatedList(list);
                Space(spaceWithin);
            }
            RPar();
        }

#if DOTNET35
		void WriteCommaSeparatedList(IEnumerable<VariableInitializer> list)
		{
			WriteCommaSeparatedList(list.SafeCast<VariableInitializer, AstNode>());
		}
		
		void WriteCommaSeparatedList(IEnumerable<AstType> list)
		{
			WriteCommaSeparatedList(list.SafeCast<AstType, AstNode>());
		}
		
		void WriteCommaSeparatedListInParenthesis(IEnumerable<Expression> list, bool spaceWithin)
		{
			WriteCommaSeparatedListInParenthesis(list.SafeCast<Expression, AstNode>(), spaceWithin);
		}
		
		void WriteCommaSeparatedListInParenthesis(IEnumerable<ParameterDeclaration> list, bool spaceWithin)
		{
			WriteCommaSeparatedListInParenthesis(list.SafeCast<ParameterDeclaration, AstNode>(), spaceWithin);
		}

#endif

        void WriteCommaSeparatedListInBrackets(IEnumerable<ParameterDeclaration> list, bool spaceWithin)
        {
            WriteToken("[", AstNode.Roles.LBracket);
            if (list.Any())
            {
                Space(spaceWithin);
                WriteCommaSeparatedList(list);
                Space(spaceWithin);
            }
            WriteToken("]", AstNode.Roles.RBracket);
        }

        void WriteCommaSeparatedListInBrackets(IEnumerable<Expression> list)
        {
            WriteToken("[", AstNode.Roles.LBracket);
            if (list.Any())
            {
                Space(policy.SpacesWithinBrackets);
                WriteCommaSeparatedList(list);
                Space(policy.SpacesWithinBrackets);
            }
            WriteToken("]", AstNode.Roles.RBracket);
        }
        #endregion

        #region Statements
        public object VisitBlockStatement(BlockStatement blockStatement, object data)
        {
            StartNode(blockStatement);
            BraceStyle style;
            if (blockStatement.Parent is AnonymousMethodExpression || blockStatement.Parent is LambdaExpression)
            {
                style = policy.AnonymousMethodBraceStyle;
            }
            else if (blockStatement.Parent is ConstructorDeclaration)
            {
                style = policy.ConstructorBraceStyle;
            }
            else if (blockStatement.Parent is DestructorDeclaration)
            {
                style = policy.DestructorBraceStyle;
            }
            else if (blockStatement.Parent is MethodDeclaration)
            {
                style = policy.MethodBraceStyle;
            }
            else if (blockStatement.Parent is Accessor)
            {
                if (blockStatement.Parent.Role == PropertyDeclaration.GetterRole)
                    style = policy.PropertyGetBraceStyle;
                else if (blockStatement.Parent.Role == PropertyDeclaration.SetterRole)
                    style = policy.PropertySetBraceStyle;
                else if (blockStatement.Parent.Role == CustomEventDeclaration.AddAccessorRole)
                    style = policy.EventAddBraceStyle;
                else if (blockStatement.Parent.Role == CustomEventDeclaration.RemoveAccessorRole)
                    style = policy.EventRemoveBraceStyle;
                else
                    throw new NotSupportedException("Unknown type of accessor");
            }
            else
            {
                style = policy.StatementBraceStyle;
            }
            OpenBrace(style);
            foreach (var node in blockStatement.Statements)
            {
                node.AcceptVisitor(this, data);
            }
            CloseBrace(style);
            NewLine();
            return EndNode(blockStatement);
        }

        public object VisitBreakStatement(BreakStatement breakStatement, object data)
        {
            StartNode(breakStatement);
            WriteKeyword("break");
            Semicolon();
            return EndNode(breakStatement);
        }

        public object VisitCheckedStatement(CheckedStatement checkedStatement, object data)
        {
            StartNode(checkedStatement);
            WriteKeyword("checked");
            checkedStatement.Body.AcceptVisitor(this, data);
            return EndNode(checkedStatement);
        }

        public object VisitContinueStatement(ContinueStatement continueStatement, object data)
        {
            StartNode(continueStatement);
            WriteKeyword("continue");
            Semicolon();
            return EndNode(continueStatement);
        }

        public object VisitDoWhileStatement(DoWhileStatement doWhileStatement, object data)
        {
            StartNode(doWhileStatement);
            WriteKeyword("do", DoWhileStatement.DoKeywordRole);
            WriteEmbeddedStatement(doWhileStatement.EmbeddedStatement);
            WriteKeyword("while", DoWhileStatement.WhileKeywordRole);
            Space(policy.SpaceBeforeWhileParentheses);
            LPar();
            Space(policy.SpacesWithinWhileParentheses);
            doWhileStatement.Condition.AcceptVisitor(this, data);
            Space(policy.SpacesWithinWhileParentheses);
            RPar();
            Semicolon();
            return EndNode(doWhileStatement);
        }

        public object VisitEmptyStatement(EmptyStatement emptyStatement, object data)
        {
            StartNode(emptyStatement);
            Semicolon();
            return EndNode(emptyStatement);
        }

        public object VisitExpressionStatement(ExpressionStatement expressionStatement, object data)
        {
            StartNode(expressionStatement);
            expressionStatement.Expression.AcceptVisitor(this, data);
            Semicolon();
            return EndNode(expressionStatement);
        }

        public object VisitFixedStatement(FixedStatement fixedStatement, object data)
        {
            StartNode(fixedStatement);
            WriteKeyword("fixed");
            Space(policy.SpaceBeforeUsingParentheses);
            LPar();
            Space(policy.SpacesWithinUsingParentheses);
            fixedStatement.Type.AcceptVisitor(this, data);
            Space();
            WriteCommaSeparatedList(fixedStatement.Variables);
            Space(policy.SpacesWithinUsingParentheses);
            RPar();
            WriteEmbeddedStatement(fixedStatement.EmbeddedStatement);
            return EndNode(fixedStatement);
        }

        public object VisitForeachStatement(ForeachStatement foreachStatement, object data)
        {
            StartNode(foreachStatement);
            foreachStatement.RangeExpression.AcceptVisitor(this, data);
            foreachStatement.BeginExpression.AcceptVisitor(this, data);
            foreachStatement.EndExpression.AcceptVisitor(this, data);
            foreachStatement.WhileStatement.AcceptVisitor(this, data);
            return EndNode(foreachStatement);
        }

        public object VisitForStatement(ForStatement forStatement, object data)
        {
            StartNode(forStatement);
            WriteKeyword("for");
            Space(policy.SpaceBeforeForParentheses);
            LPar();
            Space(policy.SpacesWithinForParentheses);

            WriteCommaSeparatedList(forStatement.Initializers);
            Space(policy.SpaceBeforeForSemicolon);
            WriteToken(";", AstNode.Roles.Semicolon);
            Space(policy.SpaceAfterForSemicolon);

            forStatement.Condition.AcceptVisitor(this, data);
            Space(policy.SpaceBeforeForSemicolon);
            WriteToken(";", AstNode.Roles.Semicolon);
            Space(policy.SpaceAfterForSemicolon);

            WriteCommaSeparatedList(forStatement.Iterators);

            Space(policy.SpacesWithinForParentheses);
            RPar();
            WriteEmbeddedStatement(forStatement.EmbeddedStatement);
            return EndNode(forStatement);
        }

        public object VisitGotoCaseStatement(GotoCaseStatement gotoCaseStatement, object data)
        {
            StartNode(gotoCaseStatement);
            WriteKeyword("goto");
            WriteKeyword("case", GotoCaseStatement.CaseKeywordRole);
            Space();
            gotoCaseStatement.LabelExpression.AcceptVisitor(this, data);
            Semicolon();
            return EndNode(gotoCaseStatement);
        }

        public object VisitGotoDefaultStatement(GotoDefaultStatement gotoDefaultStatement, object data)
        {
            StartNode(gotoDefaultStatement);
            WriteKeyword("goto");
            WriteKeyword("default", GotoDefaultStatement.DefaultKeywordRole);
            Semicolon();
            return EndNode(gotoDefaultStatement);
        }

        public object VisitGotoStatement(GotoStatement gotoStatement, object data)
        {
            StartNode(gotoStatement);
            WriteKeyword("goto");
            WriteIdentifier(gotoStatement.Label);
            Semicolon();
            return EndNode(gotoStatement);
        }

        public object VisitIfElseStatement(IfElseStatement ifElseStatement, object data)
        {
            StartNode(ifElseStatement);
            WriteKeyword("if", IfElseStatement.IfKeywordRole);
            Space(policy.SpaceBeforeIfParentheses);
            LPar();
            Space(policy.SpacesWithinIfParentheses);
            ifElseStatement.Condition.AcceptVisitor(this, data);
            Space(policy.SpacesWithinIfParentheses);
            RPar();
            WriteEmbeddedStatement(ifElseStatement.TrueStatement);
            if (!ifElseStatement.FalseStatement.IsNull)
            {
                WriteKeyword("else", IfElseStatement.ElseKeywordRole);
                WriteEmbeddedStatement(ifElseStatement.FalseStatement);
            }
            return EndNode(ifElseStatement);
        }

        public object VisitLabelStatement(LabelStatement labelStatement, object data)
        {
            StartNode(labelStatement);
            WriteIdentifier(labelStatement.Label);
            WriteToken(":", LabelStatement.Roles.Colon);
            bool foundLabelledStatement = false;
            for (AstNode tmp = labelStatement.NextSibling; tmp != null; tmp = tmp.NextSibling)
            {
                if (tmp.Role == labelStatement.Role)
                {
                    foundLabelledStatement = true;
                }
            }
            if (!foundLabelledStatement)
            {
                // introduce an EmptyStatement so that the output becomes syntactically valid
                WriteToken(";", LabelStatement.Roles.Semicolon);
            }
            NewLine();
            return EndNode(labelStatement);
        }

        public object VisitReturnStatement(ReturnStatement returnStatement, object data)
        {
            StartNode(returnStatement);
            WriteKeyword("return");
            if (!returnStatement.Expression.IsNull)
            {
                Space();
                returnStatement.Expression.AcceptVisitor(this, data);
            }
            Semicolon();
            return EndNode(returnStatement);
        }

        public object VisitSwitchStatement(SwitchStatement switchStatement, object data)
        {
            StartNode(switchStatement);
            WriteKeyword("switch");
            Space(policy.SpaceBeforeSwitchParentheses);
            LPar();
            Space(policy.SpacesWithinSwitchParentheses);
            switchStatement.Expression.AcceptVisitor(this, data);
            Space(policy.SpacesWithinSwitchParentheses);
            RPar();
            OpenBrace(policy.StatementBraceStyle);
            if (!policy.IndentSwitchBody)
                formatter.Unindent();

            foreach (var section in switchStatement.SwitchSections)
                section.AcceptVisitor(this, data);

            if (!policy.IndentSwitchBody)
                formatter.Indent();
            CloseBrace(policy.StatementBraceStyle);
            NewLine();
            return EndNode(switchStatement);
        }

        public object VisitSwitchSection(SwitchSection switchSection, object data)
        {
            StartNode(switchSection);
            bool first = true;
            foreach (var label in switchSection.CaseLabels)
            {
                if (!first)
                    NewLine();
                label.AcceptVisitor(this, data);
                first = false;
            }
            if (!(switchSection.Statements.FirstOrDefault() is BlockStatement))
                NewLine();

            if (policy.IndentCaseBody)
                formatter.Indent();

            foreach (var statement in switchSection.Statements)
                statement.AcceptVisitor(this, data);
            if (switchSection.NextSibling != null)
                NewLine();

            if (policy.IndentCaseBody)
                formatter.Unindent();

            return EndNode(switchSection);
        }

        public object VisitCaseLabel(CaseLabel caseLabel, object data)
        {
            StartNode(caseLabel);
            if (caseLabel.Expression.IsNull)
            {
                WriteKeyword("default");
            }
            else
            {
                WriteKeyword("case");
                Space();
                caseLabel.Expression.AcceptVisitor(this, data);
            }
            WriteToken(":", CaseLabel.Roles.Colon);
            return EndNode(caseLabel);
        }

        public object VisitThrowStatement(ThrowStatement throwStatement, object data)
        {
            StartNode(throwStatement);
            WriteKeyword("throw");
            if (!throwStatement.Expression.IsNull)
            {
                Space();
                throwStatement.Expression.AcceptVisitor(this, data);
            }
            Semicolon();
            return EndNode(throwStatement);
        }

        public object VisitTryCatchStatement(TryCatchStatement tryCatchStatement, object data)
        {
            StartNode(tryCatchStatement);
            WriteKeyword("try", TryCatchStatement.TryKeywordRole);
            tryCatchStatement.TryBlock.AcceptVisitor(this, data);
            foreach (var catchClause in tryCatchStatement.CatchClauses)
                catchClause.AcceptVisitor(this, data);
            if (!tryCatchStatement.FinallyBlock.IsNull)
            {
                WriteKeyword("finally", TryCatchStatement.FinallyKeywordRole);
                tryCatchStatement.FinallyBlock.AcceptVisitor(this, data);
            }
            return EndNode(tryCatchStatement);
        }

        public object VisitCatchClause(CatchClause catchClause, object data)
        {
            StartNode(catchClause);
            WriteKeyword("catch");
            if (!catchClause.Type.IsNull)
            {
                Space(policy.SpaceBeforeCatchParentheses);
                LPar();
                Space(policy.SpacesWithinCatchParentheses);
                catchClause.Type.AcceptVisitor(this, data);
                if (!string.IsNullOrEmpty(catchClause.VariableName))
                {
                    Space();
                    WriteIdentifier(catchClause.VariableName);
                }
                Space(policy.SpacesWithinCatchParentheses);
                RPar();
            }
            catchClause.Body.AcceptVisitor(this, data);
            return EndNode(catchClause);
        }

        public object VisitUncheckedStatement(UncheckedStatement uncheckedStatement, object data)
        {
            StartNode(uncheckedStatement);
            WriteKeyword("unchecked");
            uncheckedStatement.Body.AcceptVisitor(this, data);
            return EndNode(uncheckedStatement);
        }

        public object VisitUnsafeStatement(UnsafeStatement unsafeStatement, object data)
        {
            StartNode(unsafeStatement);
            WriteKeyword("unsafe");
            unsafeStatement.Body.AcceptVisitor(this, data);
            return EndNode(unsafeStatement);
        }

        public object VisitUsingNamespaceStatement(UsingNamespaceStatement usingStatement, object data)
        {
            throw new NotImplementedException();
            StartNode(usingStatement);
            WriteKeyword("using");
            WriteKeyword("namespace");

            usingStatement.ResourceAcquisition.AcceptVisitor(this, data);
            return EndNode(usingStatement);
        }

        public object VisitVariableDeclarationStatement(VariableDeclarationStatement variableDeclarationStatement, object data)
        {
            StartNode(variableDeclarationStatement);
            WriteModifiers(variableDeclarationStatement.GetChildrenByRole(VariableDeclarationStatement.ModifierRole));

            variableDeclarationStatement.Type.AcceptVisitor(this, data);

            Space();
            WriteCommaSeparatedList(variableDeclarationStatement.Variables);
            Semicolon();
            return EndNode(variableDeclarationStatement);
        }

        public object VisitWhileStatement(WhileStatement whileStatement, object data)
        {
            StartNode(whileStatement);
            WriteKeyword("while", WhileStatement.WhileKeywordRole);
            Space(policy.SpaceBeforeWhileParentheses);
            LPar();
            Space(policy.SpacesWithinWhileParentheses);
            whileStatement.Condition.AcceptVisitor(this, data);
            Space(policy.SpacesWithinWhileParentheses);
            RPar();
            WriteEmbeddedStatement(whileStatement.EmbeddedStatement);
            return EndNode(whileStatement);
        }

        public object VisitYieldBreakStatement(YieldBreakStatement yieldBreakStatement, object data)
        {
            StartNode(yieldBreakStatement);
            WriteKeyword("yield", YieldBreakStatement.YieldKeywordRole);
            WriteKeyword("break", YieldBreakStatement.BreakKeywordRole);
            Semicolon();
            return EndNode(yieldBreakStatement);
        }

        public object VisitYieldReturnStatement(YieldReturnStatement yieldReturnStatement, object data)
        {
            StartNode(yieldReturnStatement);
            WriteKeyword("yield", YieldReturnStatement.YieldKeywordRole);
            WriteKeyword("return", YieldReturnStatement.ReturnKeywordRole);
            Space();
            yieldReturnStatement.Expression.AcceptVisitor(this, data);
            Semicolon();
            return EndNode(yieldReturnStatement);
        }

        #endregion

        public object VisitMemberAccessExpression(MemberAccessExpression memberAccessExpression, object data)
        {
            throw new NotImplementedException();
        }

        public object VisitQualifiedType(QualifiedType qualifiedType, object data)
        {
            //TODO INCLUDES !!!!
            StartNode(qualifiedType);

            //if (IsIncludeChild(qualifiedType))
            //    return EndNode(qualifiedType);

            qualifiedType.Target.AcceptVisitor(this, data);
            WriteToken(".", AstNode.Roles.Dot);
            WriteIdentifier(qualifiedType.Name);
            WriteTypeArguments(qualifiedType.TypeArguments);

            return EndNode(qualifiedType);
        }


        public object VisitInterfaceMemberSpecifier(InterfaceMemberSpecifier interfaceMemberSpecifier, object data)
        {
            throw new NotImplementedException();
        }


        public object VisitComposedIdentifier(ComposedIdentifier composedIdentifier, object data)
        {
            StartNode(composedIdentifier);
            composedIdentifier.BaseIdentifier.AcceptVisitor(this, data);
            foreach (ArraySpecifier aspec in composedIdentifier.ArraySpecifiers)
                aspec.AcceptVisitor(this, data);
            return EndNode(composedIdentifier);
        }

        public object VisitPtrType(PtrType ptrType, object data)
        {
            StartNode(ptrType);
            //WriteKeyword("gc_ptr", PtrType.Roles.Keyword);
            //WriteToken("<", PtrType.Roles.LChevron);
            ptrType.Target.AcceptVisitor(this, data);
            WriteToken("*", PtrType.PointerRole);
            //WriteToken(">", PtrType.Roles.RChevron);
            return EndNode(ptrType);
        }

        private bool IsIncludeChild(AstNode member)
        {
            AstNode m = (AstNode)member;
            while (m.Parent != null)
            {
                if (m.Parent is IncludeDeclaration)
                    return true;
                m = m.Parent;
            }
            return false;
        }


        public object VisitPointerExpression(PointerExpression pointerExpression, object data)
        {
            StartNode(pointerExpression);
            LPar();
            WriteToken("*", PointerExpression.AsteriskRole);
            pointerExpression.Target.AcceptVisitor(this, data);
            return EndNode(pointerExpression);
        }


        public object VisitPointerIdentifierExpression(PointerIdentifierExpression pointerIdentifierExpression, object data)
        {
            StartNode(pointerIdentifierExpression);
            WriteToken("*", PointerExpression.AsteriskRole);
            pointerIdentifierExpression.IdentifierToken.AcceptVisitor(this, data);
            return EndNode(pointerIdentifierExpression);
        }
    }
}
