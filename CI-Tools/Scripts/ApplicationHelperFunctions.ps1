##########################
# Application Helper Functions
##########################
 
function Create-ConfigSetting {
    Param([Parameter(Mandatory=$true)]
          [int]$SettingEnumValue,
          [Parameter(Mandatory=$true)]
          [string]$SettingValue,
          [Parameter(Mandatory=$true)]
          [string]$DbConnectionString)

    $now = (get-date).ToString("u")
    
    $connection = New-Object System.Data.SqlClient.SqlConnection
    $connection.ConnectionString = $DbConnectionString
    $connection.Open()
    $command = New-Object System.Data.SQLClient.SQLCommand
    $command.CommandText = 
    "INSERT INTO [dbo].[AppConfigs]
               ([ConfigKey]
               ,[ConfigValue]
               ,[UpdateDate]
               ,[CreateDate])
         VALUES
               ($SettingEnumValue
               ,'$SettingValue'
               ,NULL
               ,'$now')"
    

    Write-Host "Creating setting..."
    $command.Connection = $connection
    $result = $command.ExecuteNonQuery()
    
    if ($result -eq 1)
    {
        Write-Host "added"
    }
    else 
    {
        Write-Error "failed"
    }
    
    
    $connection.Close()
}

function Get-ConfigSettingValue {
    Param([Parameter(Mandatory=$true)]
          [int]$SettingEnumValue,
          [Parameter(Mandatory=$true)]
          [string]$DbConnectionString)
          
    $connection = New-Object System.Data.SqlClient.SqlConnection
    $connection.ConnectionString = $DbConnectionString
    $connection.Open()
    $command = New-Object System.Data.SQLClient.SQLCommand

    $sql =     "SELECT [ConfigValue] FROM [dbo].[AppConfigs] WHERE  [ConfigKey] = $SettingEnumValue"

    $command.CommandText = $sql
    $command.Connection = $connection
    $result = $command.ExecuteScalar()
    
    $connection.Close()

    $result
}

function Create-ConfigSettingIfNotExists {
    Param([Parameter(Mandatory=$true)]
          [int]$SettingEnumValue,
          [Parameter(Mandatory=$true)]
          [string]$SettingValue,
          [Parameter(Mandatory=$true)]
          [string]$DbConnectionString) 

    $configValue = Get-ConfigSettingValue -SettingEnumValue $SettingEnumValue -DbConnectionString $DbConnectionString

    if ([string]::IsNullOrWhiteSpace($configValue))
    {
        Write-Host "Adding setting..."
        Create-ConfigSetting -SettingEnumValue $SettingEnumValue `
                             -SettingValue $SettingValue `
                             -DbConnectionString $DbConnectionString
    }
}

function Get-JsonMessageFromId {
    Param([string]$Id) 

    "{""id"":""$Id""}"
}

function Get-WebPackagePath {
    Param([Parameter(Mandatory=$true)]
          [string]$Directory,
          [Parameter(Mandatory=$true)]
          [string]$FileName) 

 "$Directory\..\$FileName"
}