echo hello1 > log.txt

zippassword=$1
bloburl=$2
storagekey=$3


hostname
uname -a

wget -qO - https://artifacts.elastic.co/GPG-KEY-elasticsearch | sudo apt-key add -
echo "deb https://artifacts.elastic.co/packages/6.x/apt stable main" | sudo tee -a /etc/apt/sources.list.d/elastic-6.x.list

sudo apt-get update
sudo apt-get upgrade -y
sudo apt-get autoremove -y

sudo apt-get install p7zip-full -y

mkdir payload
7z x payload.7z -opayload -p$zippassword


sudo apt-get install metricbeat -y
sudo systemctl enable metricbeat

sudo mv /etc/metricbeat/metricbeat.yml /etc/metricbeat/metricbeat.bak.yml
sudo cp payload/metricbeat.yml /etc/metricbeat/metricbeat.yml
sudo diff /etc/metricbeat/metricbeat.bak.yml /etc/metricbeat/metricbeat.yml || true

sudo systemctl start metricbeat


sudo apt-get install filebeat -y
sudo systemctl enable filebeat

sudo mv /etc/filebeat/filebeat.yml /etc/filebeat/filebeat.bak.yml
sudo cp payload/filebeat.yml /etc/filebeat/filebeat.yml
sudo diff /etc/filebeat/filebeat.bak.yml /etc/filebeat/filebeat.yml || true

sudo systemctl start filebeat


wget -O azcopy.tar.gz https://aka.ms/downloadazcopylinux64
tar -xf azcopy.tar.gz
sudo ./install.sh


sudo apt-get install npm nodejs -y
sudo npm install npm --global
sudo npm install -g api-spec-converter --unsafe-perm=true --allow-root
sudo npm install -g artillery --unsafe-perm=true --allow-root
sudo artillery -V
sudo artillery run payload/artillery.yml -o result.json

ls -la
ls -la payload

sudo artillery report result.json -o result.html


sudo find /var/lib/waagent -name stdout -exec cp '{}' ./stdout.txt \;
sudo find /var/lib/waagent -name errout -exec cp '{}' ./errout.txt \;
sudo cp /var/log/metricbeat/metricbeat ./metricbeat.txt
sudo cp /var/log/filebeat/filebeat ./filebeat.txt

7z a -mx9 result.7z -mhe -p$zippassword result.json result.html stdout.txt errout.txt metricbeat.txt filebeat.txt

resulturl=$bloburl/result.7z
sudo azcopy --source result.7z --destination $resulturl --dest-key $storagekey

echo hello2 >> log.txt
