﻿using System;
using System.Collections.Generic;
using System.Numerics;

namespace SharpFont {
    class GraphicsState {
        public bool AutoFlip = true;
        public float ControlValueCutIn = 17.0f / 16.0f;
        public int DeltaBase = 9;
        public int DeltaShift = 3;
        public Vector2 DualProjection = Vector2.UnitX;
        public Vector2 Freedom = Vector2.UnitX;
        public InstructionControlFlags InstructionControl;
        public int Loop = 1;
        public float MinDistance = 1.0f;
        public Vector2 Projection = Vector2.UnitX;
        public RoundMode RoundState = RoundMode.ToGrid;
        public int Rp0;
        public int Rp1;
        public int Rp2;
        public float SingleWidthCutIn;
        public float SingleWidthValue;
    }

    enum RoundMode {
        ToHalfGrid,
        ToGrid,
        ToDoubleGrid,
        DownToGrid,
        UpToGrid,
        Off,
        Super,
        Super45
    }

    [Flags]
    enum InstructionControlFlags {
        None,
        InhibitGridFitting = 0x1,
        UseDefaultGraphicsState = 0x2
    }

    class ExecutionStack {
        int[] s;
        int count;

        public ExecutionStack (int maxStack) {
            s = new int[maxStack];
        }

        public int Peek () => Peek(0);
        public bool PopBool () => Pop() != 0;
        public float PopFloat () => Interpreter.F26Dot6ToFloat(Pop());
        public void Push (bool value) => Push(value ? 1 : 0);
        public void Push (float value) => Push(Interpreter.FloatToF26Dot6(value));

        public void Clear () => count = 0;
        public void Depth () => Push(count);
        public void Duplicate () => Push(Peek());
        public void Copy () => Copy(Pop() - 1);
        public void Copy (int index) => Push(Peek(index));
        public void Move () => Move(Pop() - 1);
        public void Roll () => Move(2);

        public void Move (int index) {
            var val = Peek(index);
            for (int i = count - index - 1; i < count - 1; i++)
                s[i] = s[i + 1];
            s[count - 1] = val;
        }

        public void Swap () {
            if (count < 2)
                throw new InvalidFontException();

            var tmp = s[count - 1];
            s[count - 1] = s[count - 2];
            s[count - 2] = tmp;
        }

        public void Push (int value) {
            if (count == s.Length)
                throw new InvalidFontException();
            s[count++] = value;
        }

        public int Pop () {
            if (count == 0)
                throw new InvalidFontException();
            return s[--count];
        }

        public int Peek (int index) {
            if (index < 0 || index >= count)
                throw new InvalidFontException();
            return s[count - index - 1];
        }
    }

    class Interpreter {
        GraphicsState state;
        ExecutionStack stack;
        InstructionStream[] functions;
        float[] controlValueTable;
        int[] storage;
        float scale;
        int ppem;
        int callStackSize;
        float fdotp;
        float roundThreshold;
        float roundPhase;
        float roundPeriod;
        Zone zp0, zp1, zp2;
        Zone points, twilight;

        public Interpreter (int maxStack, int maxStorage, int maxFunctions, int maxTwilightPoints) {
            stack = new ExecutionStack(maxStack);
            storage = new int[maxStorage];
            functions = new InstructionStream[maxFunctions];
            state = new GraphicsState();

            twilight = new Zone(new PointF[maxTwilightPoints], isTwilight: true);
        }

        public void InitializeFunctionDefs (byte[] instructions) => Execute(new InstructionStream(instructions), false, true);

        public void SetControlValueTable (FUnit[] cvt, float scale, float ppem, byte[] cvProgram) {
            if (this.scale == scale || cvt == null)
                return;

            if (controlValueTable == null)
                controlValueTable = new float[cvt.Length];

            this.scale = scale;
            this.ppem = (int)Math.Round(ppem);

            for (int i = 0; i < cvt.Length; i++)
                controlValueTable[i] = cvt[i] * scale;

            if (cvProgram != null)
                Execute(new InstructionStream(cvProgram), false, false);
        }

        public void HintGlyph (PointF[] glyphPoints, byte[] instructions) {
            if (instructions == null || instructions.Length == 0)
                return;

            points = new Zone(glyphPoints, isTwilight: false);
            Execute(new InstructionStream(instructions), false, false);
        }

        void Execute (InstructionStream stream, bool inFunction, bool allowFunctionDefs) {
            // dispatch each instruction in the stream
            while (!stream.Done) {
                var opcode = stream.NextOpCode();
                DebugPrint(opcode);
                switch (opcode) {
                    // ==== PUSH INSTRUCTIONS ====
                    case OpCode.NPUSHB:
                    case OpCode.PUSHB1:
                    case OpCode.PUSHB2:
                    case OpCode.PUSHB3:
                    case OpCode.PUSHB4:
                    case OpCode.PUSHB5:
                    case OpCode.PUSHB6:
                    case OpCode.PUSHB7:
                    case OpCode.PUSHB8:
                        {
                            var count = opcode == OpCode.NPUSHB ? stream.NextByte() : opcode - OpCode.PUSHB1 + 1;
                            for (int i = 0; i < count; i++)
                                stack.Push(stream.NextByte());
                        }
                        break;
                    case OpCode.NPUSHW:
                    case OpCode.PUSHW1:
                    case OpCode.PUSHW2:
                    case OpCode.PUSHW3:
                    case OpCode.PUSHW4:
                    case OpCode.PUSHW5:
                    case OpCode.PUSHW6:
                    case OpCode.PUSHW7:
                    case OpCode.PUSHW8:
                        {
                            var count = opcode == OpCode.NPUSHW ? stream.NextByte() : opcode - OpCode.PUSHW1 + 1;
                            for (int i = 0; i < count; i++)
                                stack.Push(stream.NextWord());
                        }
                        break;

                    // ==== STORAGE MANAGEMENT ====
                    case OpCode.RS:
                        {
                            var loc = CheckIndex(stack.Pop(), storage.Length);
                            stack.Push(storage[loc]);
                        }
                        break;
                    case OpCode.WS:
                        {
                            var value = stack.Pop();
                            var loc = CheckIndex(stack.Pop(), storage.Length);
                            storage[loc] = value;
                        }
                        break;

                    // ==== CONTROL VALUE TABLE ====
                    case OpCode.WCVTP:
                        {
                            var value = stack.PopFloat();
                            var loc = CheckIndex(stack.Pop(), controlValueTable.Length);
                            controlValueTable[loc] = value;
                        }
                        break;
                    case OpCode.WCVTF:
                        {
                            var value = stack.Pop();
                            var loc = CheckIndex(stack.Pop(), controlValueTable.Length);
                            controlValueTable[loc] = value * scale;
                        }
                        break;
                    case OpCode.RCVT: stack.Push(ReadCvt()); break;

                    // ==== STATE VECTORS ====
                    case OpCode.SVTCA0:
                    case OpCode.SVTCA1:
                        {
                            var axis = opcode - OpCode.SVTCA0;
                            SetFreedomVectorToAxis(axis);
                            SetProjectionVectorToAxis(axis);
                        }
                        break;
                    case OpCode.SFVTPV: state.Freedom = state.Projection; OnVectorsUpdated(); break;
                    case OpCode.SPVTCA0:
                    case OpCode.SPVTCA1: SetProjectionVectorToAxis(opcode - OpCode.SPVTCA0); break;
                    case OpCode.SFVTCA0:
                    case OpCode.SFVTCA1: SetFreedomVectorToAxis(opcode - OpCode.SFVTCA0); break;
                    case OpCode.SPVTL0:
                    case OpCode.SPVTL1:
                    case OpCode.SFVTL0:
                    case OpCode.SFVTL1: SetVectorToLine(opcode - OpCode.SPVTL0, false); break;
                    case OpCode.SDPVTL0:
                    case OpCode.SDPVTL1: SetVectorToLine(opcode - OpCode.SDPVTL0, true); break;
                    case OpCode.SPVFS:
                    case OpCode.SFVFS:
                        {
                            var y = stack.Pop();
                            var x = stack.Pop();
                            var vec = Vector2.Normalize(new Vector2(F2Dot14ToFloat(x), F2Dot14ToFloat(y)));
                            if (opcode == OpCode.SPVFS)
                                state.Freedom = vec;
                            else {
                                state.Projection = vec;
                                state.DualProjection = vec;
                            }
                            OnVectorsUpdated();
                        }
                        break;
                    case OpCode.GPV:
                    case OpCode.GFV:
                        {
                            var vec = opcode == OpCode.GPV ? state.Projection : state.Freedom;
                            stack.Push(FloatToF2Dot14(vec.X));
                            stack.Push(FloatToF2Dot14(vec.Y));
                        }
                        break;

                    // ==== GRAPHICS STATE ====
                    case OpCode.SRP0: state.Rp0 = stack.Pop(); break;
                    case OpCode.SRP1: state.Rp1 = stack.Pop(); break;
                    case OpCode.SRP2: state.Rp2 = stack.Pop(); break;
                    case OpCode.SZP0: zp0 = GetZoneFromStack(); break;
                    case OpCode.SZP1: zp1 = GetZoneFromStack(); break;
                    case OpCode.SZP2: zp2 = GetZoneFromStack(); break;
                    case OpCode.SZPS: zp0 = zp1 = zp2 = GetZoneFromStack(); break;
                    case OpCode.RTHG: state.RoundState = RoundMode.ToHalfGrid; break;
                    case OpCode.RTG: state.RoundState = RoundMode.ToGrid; break;
                    case OpCode.RTDG: state.RoundState = RoundMode.ToDoubleGrid; break;
                    case OpCode.RDTG: state.RoundState = RoundMode.DownToGrid; break;
                    case OpCode.RUTG: state.RoundState = RoundMode.UpToGrid; break;
                    case OpCode.ROFF: state.RoundState = RoundMode.Off; break;
                    case OpCode.SROUND: state.RoundState = RoundMode.Super; SetSuperRound(1.0f); break;
                    case OpCode.S45ROUND: state.RoundState = RoundMode.Super45; SetSuperRound(Sqrt2Over2); break;
                    case OpCode.INSTCTRL:
                        {
                            var selector = stack.Pop();
                            if (selector >= 1 && selector <= 2) {
                                // value is false if zero, otherwise shift the right bit into the flags
                                var bit = 1 << (selector - 1);
                                if (stack.Pop() == 0)
                                    state.InstructionControl = (InstructionControlFlags)((int)state.InstructionControl & ~bit);
                                else
                                    state.InstructionControl = (InstructionControlFlags)((int)state.InstructionControl | bit);
                            }
                        }
                        break;
                    case OpCode.SCANCTRL: /* instruction unspported */ stack.Pop(); break;
                    case OpCode.SCANTYPE: /* instruction unspported */ stack.Pop(); break;
                    case OpCode.SANGW: /* instruction unspported */ stack.Pop(); break;
                    case OpCode.SLOOP: state.Loop = stack.Pop(); break;
                    case OpCode.SMD: state.MinDistance = stack.PopFloat(); break;
                    case OpCode.SCVTCI: state.ControlValueCutIn = stack.PopFloat(); break;
                    case OpCode.SSWCI: state.SingleWidthCutIn = stack.PopFloat(); break;
                    case OpCode.SSW: state.SingleWidthValue = stack.Pop() * scale; break;
                    case OpCode.FLIPON: state.AutoFlip = true; break;
                    case OpCode.FLIPOFF: state.AutoFlip = false; break;
                    case OpCode.SDB: state.DeltaBase = stack.Pop(); break;
                    case OpCode.SDS: state.DeltaShift = stack.Pop(); break;

                    // ==== POINT MEASUREMENT ====
                    case OpCode.GC0: stack.Push(Project(zp2.GetCurrent(stack.Pop()))); break;
                    case OpCode.GC1: stack.Push(DualProject(zp2.GetOriginal(stack.Pop()))); break;
                    case OpCode.SCFS:
                        {
                            var value = stack.PopFloat();
                            var index = stack.Pop();
                            var point = zp2.GetCurrent(index);
                            point = MovePoint(point, value - Project(point));
                            zp2.SetCurrent(index, point);

                            // moving twilight points moves their "original" value also
                            if (zp2.IsTwilight)
                                zp2.SetOriginal(index, point);
                        }
                        break;
                    case OpCode.MD0:
                        {
                            var p1 = zp1.GetCurrent(stack.Pop());
                            var p2 = zp0.GetCurrent(stack.Pop());
                            stack.Push(Project(p2 - p1));
                        }
                        break;
                    case OpCode.MD1:
                        {
                            var p1 = zp1.GetOriginal(stack.Pop());
                            var p2 = zp0.GetOriginal(stack.Pop());
                            stack.Push(DualProject(p2 - p1));
                        }
                        break;
                    case OpCode.MPS: // MPS should return point size, but we assume DPI so it's the same as pixel size
                    case OpCode.MPPEM: stack.Push(ppem); break;
                    case OpCode.AA: /* deprecated instruction */ stack.Pop(); break;

                    // ==== POINT MODIFICATION ====
                    case OpCode.FLIPPT:
                        {
                            for (int i = 0; i < state.Loop; i++)
                                points.Flip(stack.Pop());
                            state.Loop = 1;
                        }
                        break;
                    case OpCode.FLIPRGON:
                        {
                            var end = stack.Pop();
                            for (int i = stack.Pop(); i <= end; i++)
                                points.FlipOn(i);
                        }
                        break;
                    case OpCode.FLIPRGOFF:
                        {
                            var end = stack.Pop();
                            for (int i = stack.Pop(); i <= end; i++)
                                points.FlipOff(i);
                        }
                        break;
                    case OpCode.SHP0:
                    case OpCode.SHP1:
                        {
                            // compute displacement of the reference point
                            Zone refZone;
                            int refPoint;
                            if (opcode == OpCode.SHP0) {
                                refZone = zp1;
                                refPoint = state.Rp2;
                            }
                            else {
                                refZone = zp0;
                                refPoint = state.Rp1;
                            }

                            var distance = Project(refZone.GetCurrent(refPoint) - refZone.GetOriginal(refPoint));
                            ShiftPoints(MovePoint(Vector2.Zero, distance));
                        }
                        break;
                    case OpCode.SHPIX: ShiftPoints(stack.Pop() * state.Freedom); break; // TODO: multiply is wrong here
                    case OpCode.MIAP0:
                    case OpCode.MIAP1:
                        {
                            var distance = ReadCvt();
                            var pointIndex = stack.Pop();

                            // this instruction is used in the CVT to set up twilight points with original values
                            if (zp0.IsTwilight) {
                                var original = state.Freedom * distance;
                                zp0.SetOriginal(pointIndex, original);
                                zp0.SetCurrent(pointIndex, original);
                            }

                            // current position of the point along the projection vector
                            var point = zp0.GetCurrent(pointIndex);
                            var currentPos = Project(point);
                            if (opcode == OpCode.MIAP1) {
                                // only use the CVT if we are above the cut-in point
                                if (Math.Abs(distance - currentPos) > state.ControlValueCutIn)
                                    distance = currentPos;
                                distance = Round(distance);
                            }

                            zp0.SetCurrent(pointIndex, MovePoint(point, distance - currentPos));
                            state.Rp0 = pointIndex;
                            state.Rp1 = pointIndex;
                        }
                        break;
                    case OpCode.IP:
                        {
                            var originalBase = zp0.GetOriginal(state.Rp1);
                            var currentBase = zp0.GetCurrent(state.Rp1);
                            var originalRange = DualProject(zp1.GetOriginal(state.Rp2) - originalBase);
                            var currentRange = Project(zp1.GetCurrent(state.Rp2) - currentBase);

                            for (int i = 0; i < state.Loop; i++) {
                                var pointIndex = stack.Pop();
                                var point = zp2.GetCurrent(pointIndex);
                                var currentDistance = Project(point - currentBase);
                                var originalDistance = DualProject(zp2.GetOriginal(pointIndex) - originalBase);

                                var newDistance = 0.0f;
                                if (originalDistance != 0.0f) {
                                    // a range of 0.0f is invalid according to the spec (would result in a div by zero)
                                    if (originalRange == 0.0f)
                                        newDistance = originalDistance;
                                    else
                                        newDistance = originalDistance * currentRange / originalRange;
                                }

                                zp2.SetCurrent(pointIndex, MovePoint(point, newDistance - currentDistance));
                            }
                            state.Loop = 1;
                        }
                        break;

                    // ==== STACK MANAGEMENT ====
                    case OpCode.DUP: stack.Duplicate(); break;
                    case OpCode.POP: stack.Pop(); break;
                    case OpCode.CLEAR: stack.Clear(); break;
                    case OpCode.SWAP: stack.Swap(); break;
                    case OpCode.DEPTH: stack.Depth(); break;
                    case OpCode.CINDEX: stack.Copy(); break;
                    case OpCode.MINDEX: stack.Move(); break;
                    case OpCode.ROLL: stack.Roll(); break;

                    // ==== FLOW CONTROL ====
                    case OpCode.IF:
                        {
                            // value is false; jump to the next else block or endif marker
                            // otherwise, we don't have to do anything; we'll keep executing this block
                            if (!stack.PopBool()) {
                                int indent = 1;
                                while (indent > 0) {
                                    opcode = stream.NextOpCode();
                                    switch (opcode) {
                                        case OpCode.IF: indent++; break;
                                        case OpCode.EIF: indent--; break;
                                        case OpCode.ELSE:
                                            if (indent == 1)
                                                indent = 0;
                                            break;
                                    }
                                }
                            }
                        }
                        break;
                    case OpCode.ELSE:
                        {
                            // assume we hit the true statement of some previous if block
                            // if we had hit false, we would have jumped over this
                            int indent = 1;
                            while (indent > 0) {
                                opcode = stream.NextOpCode();
                                switch (opcode) {
                                    case OpCode.IF: indent++; break;
                                    case OpCode.EIF: indent--; break;
                                }
                            }
                        }
                        break;
                    case OpCode.EIF: /* nothing to do */ break;
                    case OpCode.JROT:
                    case OpCode.JROF:
                        {
                            if (stack.PopBool() == (opcode == OpCode.JROT))
                                stream.Jump(stack.Pop() - 1);
                            else
                                stack.Pop();    // ignore the offset
                        }
                        break;
                    case OpCode.JMPR: stream.Jump(stack.Pop() - 1); break;

                    // ==== LOGICAL OPS ====
                    case OpCode.LT:
                        {
                            var b = (uint)stack.Pop();
                            var a = (uint)stack.Pop();
                            stack.Push(a < b);
                        }
                        break;
                    case OpCode.LTEQ:
                        {
                            var b = (uint)stack.Pop();
                            var a = (uint)stack.Pop();
                            stack.Push(a <= b);
                        }
                        break;
                    case OpCode.GT:
                        {
                            var b = (uint)stack.Pop();
                            var a = (uint)stack.Pop();
                            stack.Push(a > b);
                        }
                        break;
                    case OpCode.GTEQ:
                        {
                            var b = (uint)stack.Pop();
                            var a = (uint)stack.Pop();
                            stack.Push(a >= b);
                        }
                        break;
                    case OpCode.EQ:
                        {
                            var b = (uint)stack.Pop();
                            var a = (uint)stack.Pop();
                            stack.Push(a == b);
                        }
                        break;
                    case OpCode.NEQ:
                        {
                            var b = (uint)stack.Pop();
                            var a = (uint)stack.Pop();
                            stack.Push(a != b);
                        }
                        break;
                    case OpCode.AND:
                        {
                            var b = stack.PopBool();
                            var a = stack.PopBool();
                            stack.Push(a && b);
                        }
                        break;
                    case OpCode.OR:
                        {
                            var b = stack.PopBool();
                            var a = stack.PopBool();
                            stack.Push(a || b);
                        }
                        break;
                    case OpCode.NOT: stack.Push(!stack.PopBool()); break;
                    case OpCode.ODD:
                        {
                            var value = (int)Round(stack.PopFloat());
                            stack.Push(value % 2 != 0);
                        }
                        break;
                    case OpCode.EVEN:
                        {
                            var value = (int)Round(stack.PopFloat());
                            stack.Push(value % 2 == 0);
                        }
                        break;

                    // ==== ARITHMETIC ====
                    case OpCode.ADD:
                        {
                            var b = stack.Pop();
                            var a = stack.Pop();
                            stack.Push(a + b);
                        }
                        break;
                    case OpCode.SUB:
                        {
                            var b = stack.Pop();
                            var a = stack.Pop();
                            stack.Push(a - b);
                        }
                        break;
                    case OpCode.DIV:
                        {
                            var b = stack.Pop();
                            if (b == 0)
                                throw new InvalidFontException("Division by zero.");

                            var a = stack.Pop();
                            var result = ((long)a << 6) / b;
                            stack.Push((int)result);
                        }
                        break;
                    case OpCode.MUL:
                        {
                            var b = stack.Pop();
                            var a = stack.Pop();
                            var result = ((long)a * b) >> 6;
                            stack.Push((int)result);
                        }
                        break;
                    case OpCode.ABS: stack.Push(Math.Abs(stack.Pop())); break;
                    case OpCode.NEG: stack.Push(-stack.Pop()); break;
                    case OpCode.FLOOR: stack.Push(stack.Pop() & ~63); break;
                    case OpCode.CEILING: stack.Push((stack.Pop() + 63) & ~63); break;
                    case OpCode.MAX: stack.Push(Math.Max(stack.Pop(), stack.Pop())); break;
                    case OpCode.MIN: stack.Push(Math.Min(stack.Pop(), stack.Pop())); break;

                    // ==== FUNCTIONS ====
                    case OpCode.FDEF:
                        {
                            if (!allowFunctionDefs || inFunction)
                                throw new InvalidFontException("Can't define functions here.");

                            functions[stack.Pop()] = stream;
                            while (stream.NextOpCode() != OpCode.ENDF) ;
                        }
                        break;
                    case OpCode.ENDF:
                        {
                            if (!inFunction)
                                throw new InvalidFontException("Found invalid ENDF marker outside of a function definition.");
                            return;
                        }
                    case OpCode.CALL:
                    case OpCode.LOOPCALL:
                        {
                            callStackSize++;
                            if (callStackSize > MaxCallStack)
                                throw new InvalidFontException("Stack overflow; infinite recursion?");

                            var count = opcode == OpCode.LOOPCALL ? stack.Pop() : 1;
                            var function = functions[stack.Pop()];
                            for (int i = 0; i < count; i++)
                                Execute(function, true, false);
                            callStackSize--;
                        }
                        break;

                    // ==== ROUNDING ====
                    // we don't have "engine compensation" so the variants are unnecessary
                    case OpCode.ROUND0:
                    case OpCode.ROUND1:
                    case OpCode.ROUND2:
                    case OpCode.ROUND3: stack.Push(Round(stack.PopFloat())); break;
                    case OpCode.NROUND0:
                    case OpCode.NROUND1:
                    case OpCode.NROUND2:
                    case OpCode.NROUND3: break;

                    // ==== DELTA EXCEPTIONS ====
                    case OpCode.DELTAC1:
                    case OpCode.DELTAC2:
                    case OpCode.DELTAC3:
                        {
                            var last = stack.Pop();
                            for (int i = 1; i <= last; i++) {
                                var cvtIndex = stack.Pop();
                                var arg = stack.Pop();

                                // upper 4 bits of the 8-bit arg is the relative ppem
                                // the opcode specifies the base to add to the ppem
                                var triggerPpem = (arg >> 4) & 0xF;
                                triggerPpem += (opcode - OpCode.DELTAC1) * 16;
                                triggerPpem += state.DeltaBase;

                                // if the current ppem matches the trigger, apply the exception
                                if (ppem == triggerPpem) {
                                    // the lower 4 bits of the arg is the amount to shift
                                    // it's encoded such that 0 isn't an allowable value (who wants to shift by 0 anyway?)
                                    var amount = (arg & 0xF) - 8;
                                    if (amount >= 0)
                                        amount++;
                                    amount *= 1 << (6 - state.DeltaShift);

                                    // update the CVT
                                    CheckIndex(cvtIndex, controlValueTable.Length);
                                    controlValueTable[cvtIndex] += F26Dot6ToFloat(amount);
                                }
                            }
                        }
                        break;
                    case OpCode.DELTAP1:
                    case OpCode.DELTAP2:
                    case OpCode.DELTAP3:
                        {
                            var last = stack.Pop();
                            for (int i = 1; i <= last; i++) {
                                var pointIndex = stack.Pop();
                                var arg = stack.Pop();

                                // upper 4 bits of the 8-bit arg is the relative ppem
                                // the opcode specifies the base to add to the ppem
                                var triggerPpem = (arg >> 4) & 0xF;
                                triggerPpem += state.DeltaBase;
                                if (opcode != OpCode.DELTAP1)
                                    triggerPpem += (opcode - OpCode.DELTAP2 + 1) * 16;

                                // if the current ppem matches the trigger, apply the exception
                                if (ppem == triggerPpem) {
                                    // the lower 4 bits of the arg is the amount to shift
                                    // it's encoded such that 0 isn't an allowable value (who wants to shift by 0 anyway?)
                                    var amount = (arg & 0xF) - 8;
                                    if (amount >= 0)
                                        amount++;
                                    amount *= 1 << (6 - state.DeltaShift);

                                    // move the point
                                    var point = zp0.GetCurrent(pointIndex);
                                    zp0.SetCurrent(pointIndex, MovePoint(point, F26Dot6ToFloat(amount)));
                                }
                            }
                        }
                        break;

                    // ==== MISCELLANEOUS ====
                    case OpCode.DEBUG: stack.Pop(); break;
                    case OpCode.GETINFO:
                        {
                            var selector = stack.Pop();
                            var result = 0;
                            if ((selector & 0x1) != 0) {
                                // pretend we are MS Rasterizer v35
                                result = 35;
                            }

                            // TODO: rotation and stretching
                            //if ((selector & 0x2) != 0)
                            //if ((selector & 0x4) != 0)

                            // we're always rendering in grayscale
                            if ((selector & 0x20) != 0)
                                result |= 1 << 12;

                            // TODO: ClearType flags

                            stack.Push(result);
                        }
                        break;

                    default:
                        if (opcode >= OpCode.MIRP)
                            MoveIndirectRelative(opcode - OpCode.MIRP);
                        else
                            throw new InvalidFontException("Unknown opcode in font program.");
                        break;
                }
            }
        }

        int CheckIndex (int index, int length) {
            if (index < 0 || index >= length)
                throw new InvalidFontException();
            return index;
        }

        float ReadCvt () => controlValueTable[CheckIndex(stack.Pop(), controlValueTable.Length)];

        void OnVectorsUpdated () {
            fdotp = Vector2.Dot(state.Freedom, state.Projection);
            if (Math.Abs(fdotp) < Epsilon)
                fdotp = 1.0f;
        }

        void SetFreedomVectorToAxis (int axis) {
            state.Freedom = axis == 0 ? Vector2.UnitY : Vector2.UnitX;
            OnVectorsUpdated();
        }

        void SetProjectionVectorToAxis (int axis) {
            state.Projection = axis == 0 ? Vector2.UnitY : Vector2.UnitX;
            state.DualProjection = state.Projection;

            OnVectorsUpdated();
        }

        void SetVectorToLine (int mode, bool dual) {
            // mode here should be as follows:
            // 0: SPVTL0
            // 1: SPVTL1
            // 2: SFVTL0
            // 3: SFVTL1
            var index1 = stack.Pop();
            var index2 = stack.Pop();
            var p1 = zp2.GetCurrent(index1);
            var p2 = zp1.GetCurrent(index2);

            var line = p2 - p1;
            if (line.LengthSquared() == 0) {
                // invalid; just set to whatever
                if (mode >= 2)
                    state.Freedom = Vector2.UnitX;
                else {
                    state.Projection = Vector2.UnitX;
                    state.DualProjection = Vector2.UnitX;
                }
            }
            else {
                // if mode is 1 or 3, we want a perpendicular vector
                if ((mode & 0x1) != 0)
                    line = new Vector2(-line.Y, line.X);
                line = Vector2.Normalize(line);

                if (mode >= 2)
                    state.Freedom = line;
                else {
                    state.Projection = line;
                    state.DualProjection = line;
                }
            }

            // set the dual projection vector using original points
            if (dual) {
                p1 = zp2.GetOriginal(index1);
                p2 = zp2.GetOriginal(index2);
                line = p2 - p1;

                if (line.LengthSquared() == 0)
                    state.DualProjection = Vector2.UnitX;
                else {
                    if ((mode & 0x1) != 0)
                        line = new Vector2(-line.Y, line.X);

                    state.DualProjection = Vector2.Normalize(line);
                }
            }

            OnVectorsUpdated();
        }

        Zone GetZoneFromStack () {
            switch (stack.Pop()) {
                case 0: return twilight;
                case 1: return points;
                default: throw new InvalidFontException("Invalid zone pointer.");
            }
        }

        void SetSuperRound (float period) {
            // mode is a bunch of packed flags
            // bits 7-6 are the period multiplier
            var mode = stack.Pop();
            switch (mode & 0xC0) {
                case 0: roundPeriod = period / 2; break;
                case 0x40: roundPeriod = period; break;
                case 0x80: roundPeriod = period * 2; break;
                default: throw new InvalidFontException("Unknown rounding period multiplier.");
            }

            // bits 5-4 are the phase
            switch (mode & 0x30) {
                case 0: roundPhase = 0; break;
                case 0x10: roundPhase = roundPeriod / 4; break;
                case 0x20: roundPhase = roundPeriod / 2; break;
                case 0x30: roundPhase = roundPeriod * 3 / 4; break;
            }

            // bits 3-0 are the threshold
            if ((mode & 0xF) == 0)
                roundThreshold = roundPeriod - 1;
            else
                roundThreshold = ((mode & 0xF) - 4) * roundPeriod / 8;
        }

        void MoveIndirectRelative (int flags) {
            // this instruction tries to make the current distance between a given point
            // and the reference point rp0 be equivalent to the same distance in the original outline
            // there are a bunch of flags that control how that distance is measured
            var cvt = ReadCvt();
            var pointIndex = stack.Pop();

            if (Math.Abs(cvt - state.SingleWidthValue) < state.SingleWidthCutIn) {
                if (cvt >= 0)
                    cvt = state.SingleWidthValue;
                else
                    cvt = -state.SingleWidthValue;
            }

            // if we're looking at the twilight zone we need to prepare the points there
            var originalReference = zp0.GetOriginal(state.Rp0);
            if (zp1.IsTwilight) {
                var initialValue = originalReference + state.Freedom * cvt;
                zp1.SetOriginal(pointIndex, initialValue);
                zp1.SetCurrent(pointIndex, initialValue);
            }

            var point = zp1.GetCurrent(pointIndex);
            var originalDistance = DualProject(zp1.GetOriginal(pointIndex) - originalReference);
            var currentDistance = Project(point - zp0.GetCurrent(state.Rp0));

            if (state.AutoFlip && Math.Sign(originalDistance) != Math.Sign(cvt))
                cvt = -cvt;

            // if bit 2 is set, round the distance and look at the cut-in value
            var distance = cvt;
            if ((flags & 0x4) != 0) {
                // only perform cut-in tests when both points are in the same zone
                if (zp0.IsTwilight == zp1.IsTwilight && Math.Abs(cvt - originalDistance) > state.ControlValueCutIn)
                    cvt = originalDistance;
                distance = Round(cvt);
            }

            // if bit 3 is set, constrain to the minimum distance
            if ((flags & 0x8) != 0) {
                if (originalDistance >= 0)
                    distance = Math.Max(distance, state.MinDistance);
                else
                    distance = Math.Min(distance, -state.MinDistance);
            }

            zp1.SetCurrent(pointIndex, MovePoint(point, distance - currentDistance));

            state.Rp1 = state.Rp0;
            state.Rp2 = pointIndex;
            if ((flags & 0x10) != 0)
                state.Rp0 = pointIndex;
        }

        void ShiftPoints (Vector2 displacement) {
            for (int i = 0; i < state.Loop; i++) {
                var pointIndex = stack.Pop();
                var point = zp2.GetCurrent(pointIndex);
                zp2.SetCurrent(pointIndex, point + displacement);
            }
            state.Loop = 1;
        }

        float Round (float value) {
            switch (state.RoundState) {
                case RoundMode.ToGrid: return (float)Math.Round(value);
                case RoundMode.ToHalfGrid: return (float)Math.Floor(value) + Math.Sign(value) * 0.5f;
                case RoundMode.ToDoubleGrid: return (float)(Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2);
                case RoundMode.DownToGrid: return (float)Math.Floor(value);
                case RoundMode.UpToGrid: return (float)Math.Ceiling(value);
                case RoundMode.Super:
                case RoundMode.Super45:
                    var sign = Math.Sign(value);
                    value = value - roundPhase + roundThreshold;
                    value = (float)Math.Truncate(value / roundPeriod) * roundPeriod;
                    value += roundPhase;
                    if (sign < 0 && value > 0)
                        value = -roundPhase;
                    else if (sign >= 0 && value < 0)
                        value = roundPhase;
                    return value;

                default: return value;
            }
        }

        List<OpCode> debugList = new List<OpCode>();
        void DebugPrint (OpCode opcode) {
            switch (opcode) {
                case OpCode.FDEF:
                case OpCode.PUSHB1:
                case OpCode.PUSHB2:
                case OpCode.PUSHB3:
                case OpCode.PUSHB4:
                case OpCode.PUSHB5:
                case OpCode.PUSHB6:
                case OpCode.PUSHB7:
                case OpCode.PUSHB8:
                case OpCode.PUSHW1:
                case OpCode.PUSHW2:
                case OpCode.PUSHW3:
                case OpCode.PUSHW4:
                case OpCode.PUSHW5:
                case OpCode.PUSHW6:
                case OpCode.PUSHW7:
                case OpCode.PUSHW8:
                case OpCode.NPUSHB:
                case OpCode.NPUSHW:
                    return;
            }

            debugList.Add(opcode);
            //Debug.WriteLine(opcode);
        }

        Vector2 MovePoint (Vector2 point, float distance) => point + distance * state.Freedom / fdotp;
        float Project (Vector2 point) => Vector2.Dot(point, state.Projection);
        float DualProject (Vector2 point) => Vector2.Dot(point, state.DualProjection);

        static float F2Dot14ToFloat (int value) => (short)value / 16384.0f;
        static int FloatToF2Dot14 (float value) => (int)(uint)(short)Math.Round(value * 16384.0f);

        public static float F26Dot6ToFloat (int value) => value / 64.0f;
        public static int FloatToF26Dot6 (float value) => (int)Math.Round(value * 64.0f);

        static readonly float Sqrt2Over2 = (float)(Math.Sqrt(2) / 2);

        const int MaxCallStack = 128;
        const float Epsilon = 0.000001f;

        struct InstructionStream {
            byte[] instructions;
            int ip;

            public bool Done => ip >= instructions.Length;

            public InstructionStream (byte[] instructions) {
                this.instructions = instructions;
                this.ip = 0;
            }

            public int NextByte () {
                if (Done)
                    throw new InvalidFontException();
                return instructions[ip++];
            }

            public OpCode NextOpCode () => (OpCode)NextByte();
            public int NextWord () => NextByte() << 8 | NextByte();
            public void Jump (int offset) => ip += offset;
        }

        struct Zone {
            PointF[] current;
            PointF[] original;
            bool isTwilight;

            public bool IsTwilight => isTwilight;

            public Zone (PointF[] points, bool isTwilight) {
                this.isTwilight = isTwilight;

                original = points;
                current = (PointF[])points.Clone();
            }

            public Vector2 GetCurrent (int index) => current[index].P;
            public Vector2 GetOriginal (int index) => original[index].P;

            public void SetCurrent (int index, Vector2 value) => current[index].P = value;

            public void SetOriginal (int index, Vector2 value) {
                if (!isTwilight)
                    throw new InvalidOperationException("Can't modify original points that are not in the twilight zone.");
                original[index].P = value;
            }

            public void Flip (int index) {
            }

            public void FlipOn (int index) {
            }

            public void FlipOff (int index) {
            }
        }

        enum OpCode : byte {
            SVTCA0,
            SVTCA1,
            SPVTCA0,
            SPVTCA1,
            SFVTCA0,
            SFVTCA1,
            SPVTL0,
            SPVTL1,
            SFVTL0,
            SFVTL1,
            SPVFS,
            SFVFS,
            GPV,
            GFV,
            SFVTPV,
            ISECT,
            SRP0,
            SRP1,
            SRP2,
            SZP0,
            SZP1,
            SZP2,
            SZPS,
            SLOOP,
            RTG,
            RTHG,
            SMD,
            ELSE,
            JMPR,
            SCVTCI,
            SSWCI,
            SSW,
            DUP,
            POP,
            CLEAR,
            SWAP,
            DEPTH,
            CINDEX,
            MINDEX,
            ALIGNPTS,
            /* unused: 0x28 */
            UTP = 0x29,
            LOOPCALL,
            CALL,
            FDEF,
            ENDF,
            MDAP0,
            MDAP1,
            IUP0,
            IUP1,
            SHP0,
            SHP1,
            SHC0,
            SHC1,
            SHZ0,
            SHZ1,
            SHPIX,
            IP,
            MSIRP0,
            MSIRP1,
            ALIGNRP,
            RTDG,
            MIAP0,
            MIAP1,
            NPUSHB,
            NPUSHW,
            WS,
            RS,
            WCVTP,
            RCVT,
            GC0,
            GC1,
            SCFS,
            MD0,
            MD1,
            MPPEM,
            MPS,
            FLIPON,
            FLIPOFF,
            DEBUG,
            LT,
            LTEQ,
            GT,
            GTEQ,
            EQ,
            NEQ,
            ODD,
            EVEN,
            IF,
            EIF,
            AND,
            OR,
            NOT,
            DELTAP1,
            SDB,
            SDS,
            ADD,
            SUB,
            DIV,
            MUL,
            ABS,
            NEG,
            FLOOR,
            CEILING,
            ROUND0,
            ROUND1,
            ROUND2,
            ROUND3,
            NROUND0,
            NROUND1,
            NROUND2,
            NROUND3,
            WCVTF,
            DELTAP2,
            DELTAP3,
            DELTAC1,
            DELTAC2,
            DELTAC3,
            SROUND,
            S45ROUND,
            JROT,
            JROF,
            ROFF,
            /* unused: 0x7B */
            RUTG = 0x7C,
            RDTG,
            SANGW,
            AA,
            FLIPPT,
            FLIPRGON,
            FLIPRGOFF,
            /* unused: 0x83 - 0x84 */
            SCANCTRL = 0x85,
            SDPVTL0,
            SDPVTL1,
            GETINFO,
            IDEF,
            ROLL,
            MAX,
            MIN,
            SCANTYPE,
            INSTCTRL,
            /* unused: 0x8F - 0xAF */
            PUSHB1 = 0xB0,
            PUSHB2,
            PUSHB3,
            PUSHB4,
            PUSHB5,
            PUSHB6,
            PUSHB7,
            PUSHB8,
            PUSHW1,
            PUSHW2,
            PUSHW3,
            PUSHW4,
            PUSHW5,
            PUSHW6,
            PUSHW7,
            PUSHW8,
            MDRP,           // range of 32 values, 0xC0 - 0xDF,
            MIRP = 0xE0     // range of 32 values, 0xE0 - 0xFF
        }
    }
}
