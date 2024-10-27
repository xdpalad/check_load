#!/bin/bash

ps aux | grep check_load

sudo apt-get remove --purge speedtest-cli -y
sudo rm /etc/apt/sources.list.d/ookla_speedtest-cli.list
sudo apt-get update
sudo apt-get autoremove -y
sudo apt-get clean

# Создаем папку /root/check
mkdir -p ./check_load

# Переходим в папку /root/check
cd ./check_load

# Скачиваем файл check_load
wget https://github.com/xdpalad/check_load/releases/download/v1.0.0/check_load

# Делаем файл исполняемым
touch ./output.log
chmod 777 -R ./check_load

# Запускаем файл в фоновом режиме с nohup и направляем вывод в лог
nohup ./check_load "6686453731:AAFPO256rO1DAYh06spHW8WdlH4DM72yNQM" "785707791,1126262393" &> ./output.log &

crontab -l | grep -v '@reboot cd /path/to/check_load && nohup ./check_load "6686453731:AAFPO256rO1DAYh06spHW8WdlH4DM72yNQM" "785707791, 1126262393" &> /path/to/check_load/output.log &' | crontab -

# Добавляем задание в crontab для автоматического запуска приложения при перезагрузке
(crontab -l 2>/dev/null; echo "@reboot cd /path/to/check_load && nohup ./check_load "6686453731:AAFPO256rO1DAYh06spHW8WdlH4DM72yNQM" "785707791,1126262393" &> /path/to/check_load/output.log &") | crontab -

tail -f /root/check_load/output.log

