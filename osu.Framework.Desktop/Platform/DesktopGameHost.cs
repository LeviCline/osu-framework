﻿// Copyright (c) 2007-2016 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu-framework/master/LICENCE

using System.Threading.Tasks;
using osu.Framework.Platform;
using OpenTK;
using osu.Framework.Input.Handlers;
using System;
using System.Collections.Generic;
using osu.Framework.Desktop.Input;
using osu.Framework.Input;

namespace osu.Framework.Desktop.Platform
{
    public abstract class DesktopGameHost : BasicGameHost
    {
        private TcpIpcProvider IpcProvider;
        private Task IpcTask;

        public DesktopGameHost(bool bindIPCPort = false)
        {
            if (bindIPCPort)
            {
                IpcProvider = new TcpIpcProvider();
                IsPrimaryInstance = IpcProvider.Bind();
                if (IsPrimaryInstance)
                {
                    IpcProvider.MessageReceived += msg => OnMessageReceived(msg);
                    IpcTask = IpcProvider.Start();
                }
            }
        }

        public override TextInputSource GetTextInput() => new GameWindowTextInput(Window);

        protected override void LoadGame(BaseGame game)
        {
            //delay load until we have a size.
            if (Size == Vector2.Zero)
            {
                UpdateScheduler.Add(delegate { LoadGame(game); });
                return;
            }

            base.LoadGame(game);
        }

        protected override async Task SendMessage(IpcMessage message)
        {
            await IpcProvider.SendMessage(message);
        }

        protected override void Dispose(bool isDisposing)
        {
            IpcProvider?.Dispose();
            IpcTask?.Wait(50);
            base.Dispose(isDisposing);
        }
    }
}
