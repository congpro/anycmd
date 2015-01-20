﻿
namespace Anycmd.Engine.Host.Ac.MemorySets
{
    using Bus;
    using Engine.Ac;
    using Engine.Ac.Abstractions;
    using Engine.Ac.Abstractions.Infra;
    using Engine.Ac.InOuts;
    using Engine.Ac.Messages.Infra;
    using Exceptions;
    using Host;
    using Infra;
    using Repositories;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using Util;

    internal sealed class ButtonSet : IButtonSet, IMemorySet
    {
        public static readonly IButtonSet Empty = new ButtonSet(EmptyAcDomain.SingleInstance);

        private readonly Dictionary<Guid, ButtonState> _dicById = new Dictionary<Guid, ButtonState>();
        private readonly Dictionary<string, ButtonState> _dicByCode = new Dictionary<string, ButtonState>(StringComparer.OrdinalIgnoreCase);
        private bool _initialized;
        private readonly IAcDomain _host;

        private readonly Guid _id = Guid.NewGuid();

        public Guid Id
        {
            get { return _id; }
        }

        internal ButtonSet(IAcDomain host)
        {
            if (host == null)
            {
                throw new ArgumentNullException("host");
            }
            if (host.Equals(EmptyAcDomain.SingleInstance))
            {
                _initialized = true;
            }
            _host = host;
            new MessageHandler(this).Register();
        }

        public bool ContainsButton(Guid buttonId)
        {
            if (!_initialized)
            {
                Init();
            }
            return _dicById.ContainsKey(buttonId);
        }

        public bool ContainsButton(string buttonCode)
        {
            if (!_initialized)
            {
                Init();
            }
            return _dicByCode.ContainsKey(buttonCode);
        }

        public bool TryGetButton(Guid buttonId, out ButtonState button)
        {
            if (!_initialized)
            {
                Init();
            }
            return _dicById.TryGetValue(buttonId, out button);
        }

        public bool TryGetButton(string code, out ButtonState button)
        {
            if (!_initialized)
            {
                Init();
            }
            return _dicByCode.TryGetValue(code, out button);
        }

        internal void Refresh()
        {
            if (_initialized)
            {
                _initialized = false;
            }
        }

        private void Init()
        {
            if (_initialized) return;
            lock (this)
            {
                if (_initialized) return;
                _host.MessageDispatcher.DispatchMessage(new MemorySetInitingEvent(this));
                _dicById.Clear();
                _dicByCode.Clear();
                var buttons = _host.RetrieveRequiredService<IOriginalHostStateReader>().GetAllButtons().ToList();
                foreach (var button in buttons)
                {
                    if (_dicById.ContainsKey(button.Id))
                    {
                        throw new AnycmdException("意外重复的按钮标识");
                    }
                    if (_dicByCode.ContainsKey(button.Code))
                    {
                        throw new AnycmdException("意外重复的按钮编码");
                    }
                    var buttonState = ButtonState.Create(button);
                    _dicById.Add(button.Id, buttonState);
                    _dicByCode.Add(button.Code, buttonState);
                }
                _initialized = true;
                _host.MessageDispatcher.DispatchMessage(new MemorySetInitializedEvent(this));
            }
        }

        public IEnumerator<ButtonState> GetEnumerator()
        {
            if (!_initialized)
            {
                Init();
            }
            return _dicById.Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            if (!_initialized)
            {
                Init();
            }
            return _dicById.Values.GetEnumerator();
        }

        #region MessageHandler
        private class MessageHandler :
            IHandler<UpdateButtonCommand>, 
            IHandler<AddButtonCommand>, 
            IHandler<ButtonAddedEvent>, 
            IHandler<ButtonUpdatedEvent>, 
            IHandler<RemoveButtonCommand>, 
            IHandler<ButtonRemovedEvent>
        {
            private readonly ButtonSet _set;

            internal MessageHandler(ButtonSet set)
            {
                _set = set;
            }

            public void Register()
            {
                var messageDispatcher = _set._host.MessageDispatcher;
                if (messageDispatcher == null)
                {
                    throw new ArgumentNullException("messageDispatcher has not be set of host:{0}".Fmt(_set._host.Name));
                }
                messageDispatcher.Register((IHandler<AddButtonCommand>)this);
                messageDispatcher.Register((IHandler<ButtonAddedEvent>)this);
                messageDispatcher.Register((IHandler<UpdateButtonCommand>)this);
                messageDispatcher.Register((IHandler<ButtonUpdatedEvent>)this);
                messageDispatcher.Register((IHandler<RemoveButtonCommand>)this);
                messageDispatcher.Register((IHandler<ButtonRemovedEvent>)this);
            }

            public void Handle(AddButtonCommand message)
            {
                Handle(message.UserSession, message.Input, isCommand: true);
            }

            public void Handle(ButtonAddedEvent message)
            {
                if (message.GetType() == typeof(PrivateButtonAddedEvent))
                {
                    return;
                }
                Handle(message.UserSession, message.Output, isCommand: false);
            }

            private void Handle(IUserSession userSession, IButtonCreateIo input, bool isCommand)
            {
                var host = _set._host;
                var dicById = _set._dicById;
                var dicByCode = _set._dicByCode;
                var buttonRepository = host.RetrieveRequiredService<IRepository<Button>>();
                if (string.IsNullOrEmpty(input.Code))
                {
                    throw new ValidationException("编码不能为空");
                }
                Button entity;
                lock (this)
                {
                    if (!input.Id.HasValue || host.ButtonSet.ContainsButton(input.Id.Value))
                    {
                        throw new AnycmdException("意外的按钮标识");
                    }
                    if (host.ButtonSet.ContainsButton(input.Code))
                    {
                        throw new ValidationException("重复的按钮编码");
                    }

                    entity = Button.Create(input);

                    var buttonState = ButtonState.Create(entity);
                    if (!dicById.ContainsKey(entity.Id))
                    {
                        dicById.Add(entity.Id, buttonState);
                    }
                    if (!dicByCode.ContainsKey(entity.Code))
                    {
                        dicByCode.Add(entity.Code, buttonState);
                    }
                    if (isCommand)
                    {
                        try
                        {
                            buttonRepository.Add(entity);
                            buttonRepository.Context.Commit();
                        }
                        catch
                        {
                            if (dicById.ContainsKey(entity.Id))
                            {
                                dicById.Remove(entity.Id);
                            }
                            if (dicByCode.ContainsKey(entity.Code))
                            {
                                dicByCode.Remove(entity.Code);
                            }
                            buttonRepository.Context.Rollback();
                            throw;
                        }
                    }
                }
                if (isCommand)
                {
                    host.MessageDispatcher.DispatchMessage(new PrivateButtonAddedEvent(userSession, entity, input));
                }
            }

            private class PrivateButtonAddedEvent : ButtonAddedEvent
            {
                internal PrivateButtonAddedEvent(IUserSession userSession, ButtonBase source, IButtonCreateIo input)
                    : base(userSession, source, input)
                {

                }
            }
            public void Handle(UpdateButtonCommand message)
            {
                Handle(message.UserSession, message.Input, isCommand: true);
            }

            public void Handle(ButtonUpdatedEvent message)
            {
                if (message.GetType() == typeof(PrivateButtonUpdatedEvent))
                {
                    return;
                }
                Handle(message.UserSession, message.Input, isCommand: false);
            }

            private void Handle(IUserSession userSession, IButtonUpdateIo input, bool isCommand)
            {
                var host = _set._host;
                var buttonRepository = host.RetrieveRequiredService<IRepository<Button>>();
                if (string.IsNullOrEmpty(input.Code))
                {
                    throw new ValidationException("编码不能为空");
                }
                ButtonState bkState;
                if (!host.ButtonSet.TryGetButton(input.Id, out bkState))
                {
                    throw new NotExistException("意外的按钮标识" + input.Id);
                }
                Button entity;
                var stateChanged = false;
                lock (bkState)
                {
                    ButtonState oldState;
                    if (!host.ButtonSet.TryGetButton(input.Id, out oldState))
                    {
                        throw new NotExistException("意外的按钮标识" + input.Id);
                    }
                    ButtonState button;
                    if (host.ButtonSet.TryGetButton(input.Code, out button) && button.Id != input.Id)
                    {
                        throw new ValidationException("重复的按钮编码");
                    }
                    entity = buttonRepository.GetByKey(input.Id);
                    if (entity == null)
                    {
                        throw new NotExistException();
                    }

                    entity.Update(input);

                    var newState = ButtonState.Create(entity);
                    stateChanged = bkState != newState;
                    if (stateChanged)
                    {
                        Update(newState);
                    }
                    if (isCommand)
                    {
                        try
                        {
                            buttonRepository.Update(entity);
                            buttonRepository.Context.Commit();
                        }
                        catch
                        {
                            if (stateChanged)
                            {
                                Update(bkState);
                            }
                            buttonRepository.Context.Rollback();
                            throw;
                        }
                    }
                }
                if (isCommand && stateChanged)
                {
                    host.MessageDispatcher.DispatchMessage(new PrivateButtonUpdatedEvent(userSession, entity, input));
                }
            }

            private void Update(ButtonState state)
            {
                var dicById = _set._dicById;
                var dicByCode = _set._dicByCode;
                var oldKey = dicById[state.Id].Code;
                var newKey = state.Code;
                dicById[state.Id] = state;
                // 如果按钮编码改变了
                if (!dicByCode.ContainsKey(newKey))
                {
                    dicByCode.Remove(oldKey);
                    dicByCode.Add(newKey, state);
                }
                else
                {
                    dicByCode[newKey] = state;
                }
            }

            private class PrivateButtonUpdatedEvent : ButtonUpdatedEvent
            {
                internal PrivateButtonUpdatedEvent(IUserSession userSession, ButtonBase source, IButtonUpdateIo input)
                    : base(userSession, source, input)
                {

                }
            }
            public void Handle(RemoveButtonCommand message)
            {
                Handle(message.UserSession, message.EntityId, isCommand: true);
            }

            public void Handle(ButtonRemovedEvent message)
            {
                if (message.GetType() == typeof(PrivateButtonRemovedEvent))
                {
                    return;
                }
                Handle(message.UserSession, message.Source.Id, isCommand: false);
            }

            private void Handle(IUserSession userSession, Guid buttonId, bool isCommand)
            {
                var host = _set._host;
                var dicById = _set._dicById;
                var dicByCode = _set._dicByCode;
                var buttonRepository = host.RetrieveRequiredService<IRepository<Button>>();
                ButtonState bkState;
                if (!host.ButtonSet.TryGetButton(buttonId, out bkState))
                {
                    return;
                }
                if (host.UiViewSet.GetUiViewButtons().Any(a => a.ButtonId == buttonId))
                {
                    throw new ValidationException("按钮关联界面视图后不能删除");
                }
                Button entity;
                lock (bkState)
                {
                    ButtonState state;
                    if (!host.ButtonSet.TryGetButton(buttonId, out state))
                    {
                        return;
                    }
                    entity = buttonRepository.GetByKey(buttonId);
                    if (entity == null)
                    {
                        return;
                    }
                    if (dicById.ContainsKey(bkState.Id))
                    {
                        if (isCommand)
                        {
                            host.MessageDispatcher.DispatchMessage(new ButtonRemovingEvent(userSession, entity));
                        }
                        dicById.Remove(bkState.Id);
                        if (dicByCode.ContainsKey(bkState.Code))
                        {
                            dicByCode.Remove(bkState.Code);
                        }
                    }
                    if (isCommand)
                    {
                        try
                        {
                            buttonRepository.Remove(entity);
                            buttonRepository.Context.Commit();
                        }
                        catch
                        {
                            if (!dicById.ContainsKey(bkState.Id))
                            {
                                dicById.Add(bkState.Id, bkState);
                            }
                            if (!dicByCode.ContainsKey(bkState.Code))
                            {
                                dicByCode.Add(bkState.Code, bkState);
                            }
                            throw;
                        }
                    }
                }
                if (isCommand)
                {
                    host.MessageDispatcher.DispatchMessage(new PrivateButtonRemovedEvent(userSession, entity));
                }
            }

            private class PrivateButtonRemovedEvent : ButtonRemovedEvent
            {
                internal PrivateButtonRemovedEvent(IUserSession userSession, ButtonBase source)
                    : base(userSession, source)
                {

                }
            }
        }
        #endregion
    }
}
