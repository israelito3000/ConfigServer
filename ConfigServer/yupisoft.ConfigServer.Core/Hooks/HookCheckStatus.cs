﻿using System;
using System.Collections.Generic;
using System.Text;

namespace yupisoft.ConfigServer.Core.Hooks
{
    public enum HookCheckStatus
    {
        Iddle,
        ChangeItem,
        DeleteItem,
        AddedItem,
        ChangeStatus,
        Notify
    }
}
