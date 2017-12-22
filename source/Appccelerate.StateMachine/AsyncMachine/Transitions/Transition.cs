//-------------------------------------------------------------------------------
// <copyright file="Transition.cs" company="Appccelerate">
//   Copyright (c) 2008-2017 Appccelerate
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
// </copyright>
//-------------------------------------------------------------------------------

namespace Appccelerate.StateMachine.AsyncMachine.Transitions
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading.Tasks;
    using Appccelerate.StateMachine.AsyncMachine.ActionHolders;
    using Appccelerate.StateMachine.AsyncMachine.GuardHolders;
    using LiteGuard = Guard;

    public class Transition<TState, TEvent>
        : ITransition<TState, TEvent>
        where TState : IComparable
        where TEvent : IComparable
    {
        private readonly List<IActionHolder> actions;
        private readonly IExtensionHost<TState, TEvent> extensionHost;
        private readonly IStateMachineInformation<TState, TEvent> stateMachineInformation;

        public Transition(IStateMachineInformation<TState, TEvent> stateMachineInformation, IExtensionHost<TState, TEvent> extensionHost)
        {
            this.stateMachineInformation = stateMachineInformation;
            this.extensionHost = extensionHost;

            this.actions = new List<IActionHolder>();
        }

        public IState<TState, TEvent> Source { get; set; }

        public IState<TState, TEvent> Target { get; set; }

        public IGuardHolder Guard { get; set; }

        public ICollection<IActionHolder> Actions => this.actions;

        private bool IsInternalTransition => this.Target == null;

        public async Task<ITransitionResult<TState, TEvent>> Fire(ITransitionContext<TState, TEvent> context)
        {
            LiteGuard.AgainstNullArgument("context", context);

            var shouldFire = await this.ShouldFire(context).ConfigureAwait(false);
            if (!shouldFire)
            {
                await this.extensionHost
                    .ForEach(extension => extension.SkippedTransition(
                        this.stateMachineInformation,
                        this,
                        context))
                    .ConfigureAwait(false);

                return TransitionResult<TState, TEvent>.NotFired;
            }

            context.OnTransitionBegin();

            await this.extensionHost
                .ForEach(extension => extension.ExecutingTransition(
                    this.stateMachineInformation,
                    this,
                    context))
                .ConfigureAwait(false);

            IState<TState, TEvent> newState = context.State;

            if (!this.IsInternalTransition)
            {
                await this.UnwindSubStates(context).ConfigureAwait(false);

                await this.Fire(this.Source, this.Target, context).ConfigureAwait(false);

                newState = await this.Target.EnterByHistory(context).ConfigureAwait(false);
            }
            else
            {
                await this.PerformActions(context).ConfigureAwait(false);
            }

            await this.extensionHost
                .ForEach(extension => extension.ExecutedTransition(
                    this.stateMachineInformation,
                    this,
                    context))
                .ConfigureAwait(false);

            return new TransitionResult<TState, TEvent>(true, newState);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "Transition from state {0} to state {1}.", this.Source, this.Target);
        }

        private static void HandleException(Exception exception, ITransitionContext<TState, TEvent> context)
        {
            context.OnExceptionThrown(exception);
        }

        /// <summary>
        /// Recursively traverses the state hierarchy, exiting states along
        /// the way, performing the action, and entering states to the target.
        /// </summary>
        /// <remarks>
        /// There exist the following transition scenarios:
        /// 0. there is no target state (internal transition)
        ///    --> handled outside this method.
        /// 1. The source and target state are the same (self transition)
        ///    --> perform the transition directly:
        ///        Exit source state, perform transition actions and enter target state
        /// 2. The target state is a direct or indirect sub-state of the source state
        ///    --> perform the transition actions, then traverse the hierarchy
        ///        from the source state down to the target state,
        ///        entering each state along the way.
        ///        No state is exited.
        /// 3. The source state is a sub-state of the target state
        ///    --> traverse the hierarchy from the source up to the target,
        ///        exiting each state along the way.
        ///        Then perform transition actions.
        ///        Finally enter the target state.
        /// 4. The source and target state share the same super-state
        /// 5. All other scenarios:
        ///    a. The source and target states reside at the same level in the hierarchy
        ///       but do not share the same direct super-state
        ///    --> exit the source state, move up the hierarchy on both sides and enter the target state
        ///    b. The source state is lower in the hierarchy than the target state
        ///    --> exit the source state and move up the hierarchy on the source state side
        ///    c. The target state is lower in the hierarchy than the source state
        ///    --> move up the hierarchy on the target state side, afterward enter target state
        /// </remarks>
        /// <param name="source">The source state.</param>
        /// <param name="target">The target state.</param>
        /// <param name="context">The event context.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task Fire(IState<TState, TEvent> source, IState<TState, TEvent> target, ITransitionContext<TState, TEvent> context)
        {
            if (source == this.Target)
            {
                // Handles 1.
                // Handles 3. after traversing from the source to the target.
                await source.Exit(context).ConfigureAwait(false);
                await this.PerformActions(context).ConfigureAwait(false);
                await this.Target.Entry(context).ConfigureAwait(false);
            }
            else if (source == target)
            {
                // Handles 2. after traversing from the target to the source.
                await this.PerformActions(context).ConfigureAwait(false);
            }
            else if (source.SuperState == target.SuperState)
            {
                //// Handles 4.
                //// Handles 5a. after traversing the hierarchy until a common ancestor if found.
                await source.Exit(context).ConfigureAwait(false);
                await this.PerformActions(context).ConfigureAwait(false);
                await target.Entry(context).ConfigureAwait(false);
            }
            else
            {
                // traverses the hierarchy until one of the above scenarios is met.

                // Handles 3.
                // Handles 5b.
                if (source.Level > target.Level)
                {
                    await source.Exit(context).ConfigureAwait(false);
                    await this.Fire(source.SuperState, target, context).ConfigureAwait(false);
                }
                else if (source.Level < target.Level)
                {
                    // Handles 2.
                    // Handles 5c.
                    await this.Fire(source, target.SuperState, context).ConfigureAwait(false);
                    await target.Entry(context).ConfigureAwait(false);
                }
                else
                {
                    // Handles 5a.
                    await source.Exit(context).ConfigureAwait(false);
                    await this.Fire(source.SuperState, target.SuperState, context).ConfigureAwait(false);
                    await target.Entry(context).ConfigureAwait(false);
                }
            }
        }

        private async Task<bool> ShouldFire(ITransitionContext<TState, TEvent> context)
        {
            try
            {
                return this.Guard == null || await this.Guard.Execute(context.EventArgument).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await this.extensionHost
                    .ForEach(extention => extention.HandlingGuardException(this.stateMachineInformation, this, context, ref exception))
                    .ConfigureAwait(false);

                HandleException(exception, context);

                await this.extensionHost
                    .ForEach(extention => extention.HandledGuardException(this.stateMachineInformation, this, context, exception))
                    .ConfigureAwait(false);

                return false;
            }
        }

        private async Task PerformActions(ITransitionContext<TState, TEvent> context)
        {
            foreach (IActionHolder action in this.actions)
            {
                try
                {
                    await action.Execute(context.EventArgument).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await this.extensionHost
                        .ForEach(extension => extension.HandlingTransitionException(this.stateMachineInformation, this, context, ref exception))
                        .ConfigureAwait(false);

                    HandleException(exception, context);

                    await this.extensionHost
                        .ForEach(extension => extension.HandledTransitionException(this.stateMachineInformation, this, context, exception))
                        .ConfigureAwait(false);
                }
            }
        }

        private async Task UnwindSubStates(ITransitionContext<TState, TEvent> context)
        {
            for (IState<TState, TEvent> o = context.State; o != this.Source; o = o.SuperState)
            {
                await o.Exit(context).ConfigureAwait(false);
            }
        }
    }
}